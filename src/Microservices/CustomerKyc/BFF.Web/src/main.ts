import { HttpAdapterHost, NestFactory } from '@nestjs/core';
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
import { answerOutage, OUTAGE_STYLESHEET_PATH, OutageExceptionFilter, outageStylesheetRoute } from './resilience/outage';

// ┌─ :: ARCHITECTURE + SECURITY :: KYC BFF (NestJS): Backend-for-Frontend ─────────────────────────
// │ Why : The officer's browser must never hold a token, and the BFF must be as hardened as the
// │       .NET ones.
// │ How : Server-side session in PostgreSQL behind a __Host- cookie (HttpOnly, Secure,
// │       SameSite=Lax, 30 minutes rolling); strict CSP by hash; nosniff; framed only by the Shell;
// │       HTTPS only (its own certificate, or the ingress's with KYC_BFF_TRUSTED_PROXIES).
// │ Ref : Features 2.1.1, 2.3 | BFF pattern | OWASP A05, A07 | ASVS V3
// └────────────────────────────────────────────────────────────────────────────────────────────────
async function bootstrap() {
  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ A friendly console window title, like the .NET services (Common/Observability/
  // │ ConsoleTitle.cs): on Windows process.title names the console window - set here, in the
  // │ service itself, so pnpm and node do not leave their own command line as the title.
  // └──────────────────────────────────────────────────────────────────────────────────────
  process.title = 'Customer KYC BFF';

  const options = loadOptions();
  const logger = new EwpLogger('customer-kyc-bff');

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Plain HTTP (TLS at the ingress) only together with the ingress's addresses: express sees
  // │ HTTPS only through their X-Forwarded-Proto, and express-session sets the Secure session
  // │ cookie only then - without them no sign-in could ever complete.
  // └──────────────────────────────────────────────────────────────────────────────────────
  if (!options.tls && !options.trustProxy) {
    throw new Error('KYC_BFF_PLAIN_HTTP=true needs KYC_BFF_TRUSTED_PROXIES (the ingress that ends TLS); the KYC BFF refuses to start.');
  }

  const app = await NestFactory.create(AppModule, {
    // HTTPS with the development certificate, or plain HTTP behind the ingress (listener-tls.ts).
    httpsOptions: options.tls,
    logger,
  });

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Forwarded headers only from the ingress, one hop (trusted-proxies.ts); the Host header
  // │ is never taken from X-Forwarded-Host (express would use it for req.hostname).
  // └──────────────────────────────────────────────────────────────────────────────────────
  const http = app.getHttpAdapter().getInstance() as express.Express;
  if (options.trustProxy) http.set('trust proxy', options.trustProxy);
  app.use((req: express.Request, _res: express.Response, next: express.NextFunction) => {
    delete req.headers['x-forwarded-host'];
    next();
  });

  // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
  // │ Telemetry first, so every request - static files included - is traced, timed and logged.
  // └──────────────────────────────────────────────────────────────────────────────────────
  app.use(traceMiddleware);
  app.use(requestMetrics);
  app.use(requestLogging(logger));

  // Sessions in PostgreSQL (EwpBffStateDb, schema kyc_bff), encrypted at rest (session-store.ts).
  const store = sessionStore(options.databaseUrl, options.sessionKey);

  // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
  // │ Probes and the Prometheus scrape, like the .NET components: anonymous, no session, no
  // │ internals in the body. Live: the process answers. Ready: the session database answers too.
  // └──────────────────────────────────────────────────────────────────────────────────────
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
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Like Observability:Metrics:Enabled in .NET: on in development, off in production unless
  // │ KYC_BFF_METRICS_ENABLED=true - the scrape is anonymous, so only the cluster's scraper may reach it.
  // └──────────────────────────────────────────────────────────────────────────────────────
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

  // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
  // │ The MFE's Shell origin, set at run time from KYC_BFF_SHELL_ORIGIN (the same contract as
  // │ the .NET BFFs' MfeRuntimeConfig.cs): the export loads /ewp-runtime-config.js before Next.js starts the page
  // │ and lib/shell-origin.ts reads it, so one image serves every environment. JSON-encoded,
  // │ frozen, not cached; no secret, so no session needed.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const runtimeConfig = `window.__EWP_RUNTIME__ = Object.freeze(${JSON.stringify({ shellOrigin: new URL(options.shellOrigin).origin })});`;
  http.get('/ewp-runtime-config.js', (_req, res) => {
    res.setHeader('Content-Type', 'text/javascript; charset=utf-8');
    res.setHeader('Cache-Control', 'no-cache');
    res.send(runtimeConfig);
  });

  // ┌─ PERF ───────────────────────────────────────────────────────────────────────────────
  // │ Next.js 16 export: a page prefetches "__next.v1.kyc.cases.__PAGE__.txt" but the export
  // │ writes "__next.v1/kyc/cases/__PAGE__.txt" - every prefetch answered 404. Rewrite to the
  // │ nested file when the dotted one is missing and the nested one exists (segments of
  // │ letters, digits, "_" and "-" only; express.static still guards the root). Same rule as
  // │ Common/WebUtilities/Helpers/NextExportSegmentFiles.cs for the .NET BFFs.
  // │ SEC: the file checks themselves stay inside the static root too (a "../" in the path
  // │ must not probe for files elsewhere on the disk).
  // └──────────────────────────────────────────────────────────────────────────────────────
  const staticRoot = path.resolve(options.staticRoot);
  const insideStaticRoot = (urlPath: string): string | undefined => {
    const file = path.resolve(staticRoot, `.${urlPath}`);
    return file.startsWith(staticRoot + path.sep) ? file : undefined;
  };
  app.use((req: express.Request, _res: express.Response, next: express.NextFunction) => {
    if (req.method === 'GET' || req.method === 'HEAD') {
      const slash = req.path.lastIndexOf('/');
      const match = /^__next\.([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+)\.txt$/.exec(req.path.slice(slash + 1));
      const dotted = match ? insideStaticRoot(req.path) : undefined;
      if (match && dotted && !fs.existsSync(dotted)) {
        const segments = match[1].split('.');
        const nested = `${req.path.slice(0, slash + 1)}__next.${segments[0]}/${segments.slice(1).join('/')}.txt`;
        const nestedFile = insideStaticRoot(nested);
        if (nestedFile && fs.existsSync(nestedFile)) req.url = nested + req.url.slice(req.path.length);
      }
    }
    next();
  });

  app.use(
    express.static(staticRoot, {
      extensions: ['html'],
    })
  );

  // The outage page's stylesheet: before the session, so it is served during an outage too.
  app.use(OUTAGE_STYLESHEET_PATH, outageStylesheetRoute);

  app.use(
    session({
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ __Host- session cookie: HttpOnly, Secure, SameSite=Lax, 30 minutes rolling - locked
      // │ to this host over HTTPS.
      // └──────────────────────────────────────────────────────────────────────────────────────
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
  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ Right after the session: a session store that cannot reach the database is answered
  // │ with 503 + Retry-After, not Express's bare 500 page; the controllers' sign-in and calls get
  // │ the same answer when the database or the IDP is unavailable (outage.ts).
  // └──────────────────────────────────────────────────────────────────────────────────────
  app.use(answerOutage(logger));
  app.useGlobalFilters(new OutageExceptionFilter(logger, app.get(HttpAdapterHost).httpAdapter));

  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
    })
  );

  await app.listen(options.port, '0.0.0.0');

  logger.log(`Customer KYC BFF listening on port ${options.port} (${options.tls ? 'https' : 'plain http behind the ingress'})`, 'Bootstrap');
}

void bootstrap();