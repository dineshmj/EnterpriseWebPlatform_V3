import * as fs from 'node:fs';
import { NestFactory } from '@nestjs/core';
import type { NextFunction, Request, Response } from 'express';
import { AppModule } from './app.module';
import { loadOptions } from './configuration/journey-options';
import { ProblemDetailsFilter } from './http/problem-details.filter';
import { overallRateLimit, RateLimiter } from './http/rate-limit';

/**
 * Audit Journey API (NestJS) - the customer's "Journey API" tier: an Experience / Edge API,
 * deployed on its own, between the Audit web BFF and the Domain APIs. It holds no data and no
 * business rules; it composes Domain API calls for one screen, always acting for the person.
 */
async function bootstrap() {
  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ A friendly console window title, like the .NET services (Common/Observability/
  // │ ConsoleTitle.cs): on Windows process.title names the console window - set here, in the
  // │ service itself, so pnpm and node do not leave their own command line as the title.
  // └──────────────────────────────────────────────────────────────────────────────────────
  process.title = 'Audit Journey API';

  const options = loadOptions();

  const app = await NestFactory.create(AppModule, {
    httpsOptions: { pfx: fs.readFileSync(options.tlsPfxPath), passphrase: options.tlsPfxPassword },
  });

  // A JSON API for one server-side caller: no browser should ever render or frame it.
  app.use((_req: Request, res: Response, next: NextFunction) => {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ An API serves no pages: CSP default-src 'none' and never framed.
    // └──────────────────────────────────────────────────────────────────────────────────────
    res.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'");
    res.setHeader('Cache-Control', 'no-store');
    next();
  });

  // Errors as Problem Details (RFC 9457); an overall request limit before any token is checked.
  app.useGlobalFilters(new ProblemDetailsFilter());
  app.use(overallRateLimit(new RateLimiter()));

  // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
  // │ One log line per request: method, path without the query string, status, duration.
  // └──────────────────────────────────────────────────────────────────────────────────────
  app.use((req: Request, res: Response, next: NextFunction) => {
    const started = Date.now();
    res.on('finish', () => {
      if (!req.path.startsWith('/health')) {
        console.log(`HTTP ${req.method} ${req.path} responded ${res.statusCode} in ${Date.now() - started} ms`);
      }
    });
    next();
  });

  app.enableShutdownHooks();
  await app.listen(options.port, '0.0.0.0');
  console.log(`Audit Journey API listening on ${options.publicOrigin}`);
}

void bootstrap();