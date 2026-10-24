import type * as express from 'express';
import { type ArgumentsHost, Catch, HttpException, type LoggerService } from '@nestjs/common';
import { BaseExceptionFilter } from '@nestjs/core';

// ┌─ :: RESILIENCE :: An outage of a dependency answered quickly and plainly ──────────────────────
// │ Why : The session lives in PostgreSQL and every sign-in goes through the IDP. While one of them
// │       is unreachable, express-session hands the store's error on (Express's bare 500 page) and
// │       a failed sign-in becomes Nest's 500 - inside the Shell's frame, or to the MFE's calls.
// │ How : The session pool gives up after 5 s (session-store.ts). An Express error handler right
// │       after the session middleware, and a global Nest exception filter for the controllers
// │       (sign-in, sign-out, the API), answer "a dependency is unavailable" with 503 + Retry-After -
// │       a page that tries again by itself for a page load, Problem Details for a call - the same
// │       answer as the .NET hosts and the Next.js BFFs. Unavailable = the database or the IDP
// │       cannot be reached, times out, or (the IDP) answers 502 / 503 / 504. Every other error
// │       keeps its normal handling.
// │ Ref : doc/Containers.md | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
export const RETRY_AFTER_SECONDS = 15;

/** The outage page's stylesheet path: answered before the session middleware, so it needs no dependency. */
export const OUTAGE_STYLESHEET_PATH = '/ewp-outage.css';

// The CSP allows stylesheets from 'self' only, no inline style. The same rules as
// Common.WebUtilities/Resilience/DependencyOutage.cs and the Next.js BFFs' server.mjs.
const outageStylesheet = `:root { color-scheme: light dark; }
body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f4f6f8; color: #1f2933;
       font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
main { max-width: 32rem; margin: 1.5rem; padding: 2rem 2.25rem; background: #fff; border-radius: 12px;
       border-top: 4px solid #d97706; box-shadow: 0 1px 3px rgba(0, 0, 0, .08), 0 8px 24px rgba(0, 0, 0, .06); }
h1 { margin: 0 0 .75rem; font-size: 1.35rem; font-weight: 600; }
p { margin: .5rem 0; line-height: 1.5; }
.ref { font-size: .8rem; color: #6b7280; }
@media (prefers-color-scheme: dark) {
  body { background: #111827; color: #e5e7eb; }
  main { background: #1f2937; box-shadow: none; }
  .ref { color: #9ca3af; }
}`;

/** Serves the outage page's stylesheet; register it BEFORE the session middleware. */
export function outageStylesheetRoute(_req: express.Request, res: express.Response): void {
  res.set({ 'Cache-Control': 'public, max-age=86400', 'X-Content-Type-Options': 'nosniff' }).type('css').send(outageStylesheet);
}

// Postgres answers that mean the server is going away, starting up or full - not a query problem.
const outageSqlStates = new Set(['57P01', '57P02', '57P03', '53300']);
const outageNetworkCodes = new Set(['ECONNREFUSED', 'ECONNRESET', 'ETIMEDOUT', 'EHOSTUNREACH', 'ENETUNREACH', 'ENOTFOUND', 'EAI_AGAIN', 'EPIPE']);
// A dependency saying it cannot serve: bad gateway, service unavailable, gateway timeout.
const outageStatuses = new Set([502, 503, 504]);

/**
 * True when the error (or one it wraps) means a dependency cannot serve the request at all: the
 * database or the IDP cannot be reached, times out, or (the IDP) answers 502 / 503 / 504.
 */
export function isDependencyUnavailable(error: unknown, depth = 0): boolean {
  if (!error || typeof error !== 'object' || depth > 5) return false;
  const e = error as { code?: unknown; message?: unknown; cause?: unknown; errors?: unknown; response?: { statusCode?: unknown } };
  const code = typeof e.code === 'string' ? e.code : '';
  if (outageNetworkCodes.has(code) || outageSqlStates.has(code) || code.startsWith('08')) return true;
  const message = typeof e.message === 'string' ? e.message : '';
  // pg: no connection within the pool's timeout, or it dropped; openid-client: its own request timeout.
  if (/timeout exceeded when trying to connect|Connection terminated|outgoing request timed out/i.test(message)) return true;
  // openid-client's OPError carries the IDP's answer: 502 / 503 / 504 = the IDP is unavailable.
  if (typeof e.response?.statusCode === 'number' && outageStatuses.has(e.response.statusCode)) return true;
  // Node's connect to "localhost" tries IPv6 and IPv4: an AggregateError holds both refusals.
  if (Array.isArray(e.errors) && e.errors.some((inner) => isDependencyUnavailable(inner, depth + 1))) return true;
  return isDependencyUnavailable(e.cause, depth + 1);
}

function writeOutageAnswer(req: express.Request, res: express.Response): void {
  res.status(503).set({ 'Retry-After': String(RETRY_AFTER_SECONDS), 'Cache-Control': 'no-store' });
  const fetchMode = req.get('sec-fetch-mode');
  const pageLoad = fetchMode ? fetchMode === 'navigate' : (req.get('accept') ?? '').includes('text/html');
  if (pageLoad) {
    // No script, no inline style (it renders under this BFF's CSP; the look comes from the
    // stylesheet above); the reload is a meta refresh.
    res.type('html').send(
      `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">` +
        `<meta http-equiv="refresh" content="${RETRY_AFTER_SECONDS}"><title>Temporarily unavailable</title>` +
        `<link rel="stylesheet" href="${OUTAGE_STYLESHEET_PATH}"></head><body><main>` +
        `<h1>Temporarily unavailable</h1><p>A service this page depends on is not answering. ` +
        `This page tries again by itself in ${RETRY_AFTER_SECONDS} seconds.</p></main></body></html>`,
    );
    return;
  }
  res.type('application/problem+json').send(JSON.stringify({
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
    title: 'Temporarily unavailable',
    status: 503,
    detail: `A service this request depends on is not answering. Try again in ${RETRY_AFTER_SECONDS} seconds.`,
  }));
}

/** Express error handler (after the session middleware): a dependency's outage becomes 503; anything else goes on. */
export function answerOutage(logger: LoggerService): express.ErrorRequestHandler {
  return (error: unknown, req: express.Request, res: express.Response, next: express.NextFunction) => {
    if (!isDependencyUnavailable(error) || res.headersSent) return next(error);
    logger.warn(`Dependency unavailable: ${req.method} ${req.path} answered 503 (${(error as Error).message})`, 'Outage');
    writeOutageAnswer(req, res);
  };
}

/** Global Nest filter for the controllers: a dependency's outage becomes 503; anything else gets Nest's own handling. */
@Catch()
export class OutageExceptionFilter extends BaseExceptionFilter {
  constructor(private readonly logger: LoggerService, ...rest: ConstructorParameters<typeof BaseExceptionFilter>) {
    super(...rest);
  }

  override catch(exception: unknown, host: ArgumentsHost): void {
    const http = host.switchToHttp();
    const req = http.getRequest<express.Request>();
    const res = http.getResponse<express.Response>();
    // NOTE: an HttpException is the controller's own answer (e.g. a breaker's 503 with its message):
    // never replaced - only raw errors from the database or the IDP are.
    if (host.getType() !== 'http' || exception instanceof HttpException || !isDependencyUnavailable(exception) || res.headersSent) {
      super.catch(exception, host);
      return;
    }
    this.logger.warn(`Dependency unavailable: ${req.method} ${req.path} answered 503 (${(exception as Error).message})`, 'Outage');
    writeOutageAnswer(req, res);
  }
}