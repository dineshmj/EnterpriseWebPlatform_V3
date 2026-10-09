import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import session from 'express-session';
import * as express from 'express';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { AppModule } from './app.module';
import { loadOptions } from './configuration/kyc-bff-options';
import { buildContentSecurityPolicy } from './security/content-security-policy';
import { sessionStore } from './auth/session-store';
import { EwpLogger, requestLogging } from './observability/logger';
import { healthStatus, metricsEndpoint, requestMetrics } from './observability/metrics';
import { traceMiddleware } from './observability/trace-context';

async function bootstrap() {
  const options = loadOptions();
  const logger = new EwpLogger('customer-kyc-bff');

  const app = await NestFactory.create(AppModule, {
    httpsOptions: createHttpsOptions(
      options.tlsPfxPath,
      options.tlsPfxPassword
    ),
    logger,
  });

  // Telemetry first, so every request - static files included - is traced, timed and logged.
  app.use(traceMiddleware);
  app.use(requestMetrics);
  app.use(requestLogging(logger));

  // Sessions in PostgreSQL (EwpBffStateDb, schema kyc_bff), encrypted at rest (session-store.ts).
  const store = sessionStore(options.databaseUrl, options.sessionKey);

  // Probes and the Prometheus scrape, like the .NET components: anonymous, no session, no
  // internals in the body. Live: the process answers. Ready: the session database answers too.
  const http = app.getHttpAdapter().getInstance() as express.Express;
  http.get('/health/live', (_req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.json({ status: 'Healthy', checks: [{ name: 'self', status: 'Healthy' }] });
  });
  http.get('/health/ready', async (_req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    try {
      await store.ping();
      healthStatus.set({ check: 'session-database' }, 2);
      res.json({ status: 'Healthy', checks: [{ name: 'session-database', status: 'Healthy' }] });
    } catch {
      healthStatus.set({ check: 'session-database' }, 0);
      res.status(503).json({ status: 'Unhealthy', checks: [{ name: 'session-database', status: 'Unhealthy', description: 'The session database is not reachable.' }] });
    }
  });
  // Like Observability:Metrics:Enabled in .NET: on in development, off in production unless
  // KYC_BFF_METRICS_ENABLED=true - the scrape is anonymous, so only the cluster's scraper may reach it.
  const metricsEnabled = process.env.KYC_BFF_METRICS_ENABLED
    ? process.env.KYC_BFF_METRICS_ENABLED === 'true'
    : process.env.NODE_ENV !== 'production';
  if (metricsEnabled) http.get('/metrics', metricsEndpoint);

  // Browser security headers. This MFE may be framed only by the Shell, and by
  // the IDP (which loads /signout-oidc in a hidden iframe for front-channel logout).
  // It frames only its own PDF preview ('self'). The evidence endpoint overrides
  // this header with its own, narrower policy.
  const idpOrigin = new URL(options.authority).origin;
  const csp = buildContentSecurityPolicy(
    path.resolve(options.staticRoot),
    [options.shellOrigin, idpOrigin],
    ["'self'"],
  );
  // KYC_BFF_CSP_REPORT_ONLY=true reports violations in the browser console instead of blocking.
  const cspHeader = process.env.KYC_BFF_CSP_REPORT_ONLY === 'true'
    ? 'Content-Security-Policy-Report-Only'
    : 'Content-Security-Policy';
  app.use((_req: express.Request, res: express.Response, next: express.NextFunction) => {
    res.setHeader(cspHeader, csp);
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
    next();
  });

  app.use(
    express.static(path.resolve(options.staticRoot), {
      extensions: ['html'],
    })
  );

  app.use(
    session({
      name: '__Host-KYC-BFF-SESSION',
      secret: options.sessionSecret,
      // PostgreSQL, shared by every instance and by back-channel logout (session-store.ts).
      store,
      resave: false,
      saveUninitialized: false,
      // Sliding 30 minutes, like the .NET BFFs: every request renews the cookie and the row.
      rolling: true,
      cookie: {
        httpOnly: true,
        secure: true,
        // Lax: never sent on cross-site sub-requests (second CSRF defence besides the
        // X-CSRF-Token check). The Shell, this MFE and the IDP are one site, so the
        // framed MFE and the IDP redirect back to the callback still carry it.
        sameSite: 'lax',
        path: '/',
        maxAge: 30 * 60 * 1000,
      },
    })
  );

  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
    })
  );

  await app.listen(options.port, '0.0.0.0');

  logger.log(`Customer KYC BFF listening on https://kyc.dev.localhost:${options.port}`, 'Bootstrap');
}

function createHttpsOptions(pfxPath?: string, pfxPassword?: string) {
  if (!pfxPath) {
    throw new Error(
      'KYC_BFF_TLS_PFX_PATH must be configured because the KYC BFF is embedded by the HTTPS Shell.'
    );
  }

  const resolved = path.resolve(pfxPath);

  if (!fs.existsSync(resolved)) {
    throw new Error(`KYC BFF TLS PFX file was not found: ${resolved}`);
  }

  return {
    pfx: fs.readFileSync(resolved),
    passphrase: pfxPassword,
  };
}

void bootstrap();