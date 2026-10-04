import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import session from 'express-session';
import * as express from 'express';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { AppModule } from './app.module';
import { loadOptions } from './configuration/kyc-bff-options';
import { buildContentSecurityPolicy } from './security/content-security-policy';
import { sessionStore } from './auth/session-registry';

async function bootstrap() {
  const options = loadOptions();

  const app = await NestFactory.create(AppModule, {
    httpsOptions: createHttpsOptions(
      options.tlsPfxPath,
      options.tlsPfxPassword
    ),
  });

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
      // Shared with the back-channel logout endpoint (session-registry.ts).
      store: sessionStore,
      resave: false,
      saveUninitialized: false,
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

  console.log(
    `Customer KYC BFF listening on https://kyc.dev.localhost:${options.port}`
  );
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