import type * as express from 'express';
import type { LoggerService } from '@nestjs/common';

// ┌─ :: RESILIENCE :: A database outage answered quickly and plainly ──────────────────────────────
// │ Why : The session lives in PostgreSQL. While it is unreachable, express-session hands the
// │       store's error on and Express answers with its bare 500 page - inside the Shell's frame,
// │       or to the MFE's API calls.
// │ How : The session pool gives up after 5 s (session-store.ts). An error handler right after
// │       the session middleware answers "cannot reach the database" with 503 + Retry-After - a
// │       page that tries again by itself for a page load, Problem Details for a call - the same
// │       answer as the .NET hosts and the Next.js BFFs. Every other error is passed on unchanged.
// │ Ref : doc/Containers.md | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
export const RETRY_AFTER_SECONDS = 15;

// Postgres answers that mean the server is going away, starting up or full - not a query problem.
const outageSqlStates = new Set(['57P01', '57P02', '57P03', '53300']);
const outageNetworkCodes = new Set(['ECONNREFUSED', 'ECONNRESET', 'ETIMEDOUT', 'EHOSTUNREACH', 'ENETUNREACH', 'ENOTFOUND', 'EAI_AGAIN', 'EPIPE']);

/** True when the error (or one it wraps) means the database cannot be reached at all. */
export function isDatabaseUnavailable(error: unknown, depth = 0): boolean {
  if (!error || typeof error !== 'object' || depth > 5) return false;
  const e = error as { code?: unknown; message?: unknown; cause?: unknown; errors?: unknown };
  const code = typeof e.code === 'string' ? e.code : '';
  if (outageNetworkCodes.has(code) || outageSqlStates.has(code) || code.startsWith('08')) return true;
  const message = typeof e.message === 'string' ? e.message : '';
  if (/timeout exceeded when trying to connect|Connection terminated/i.test(message)) return true;
  // Node's connect to "localhost" tries IPv6 and IPv4: an AggregateError holds both refusals.
  if (Array.isArray(e.errors) && e.errors.some((inner) => isDatabaseUnavailable(inner, depth + 1))) return true;
  return isDatabaseUnavailable(e.cause, depth + 1);
}

/** Express error handler: a database outage becomes 503 + Retry-After; anything else goes on. */
export function answerDatabaseOutage(logger: LoggerService): express.ErrorRequestHandler {
  return (error: unknown, req: express.Request, res: express.Response, next: express.NextFunction) => {
    if (!isDatabaseUnavailable(error) || res.headersSent) return next(error);
    logger.warn(`Database unreachable: ${req.method} ${req.path} answered 503 (${(error as Error).message})`, 'DatabaseOutage');

    res.status(503).set({ 'Retry-After': String(RETRY_AFTER_SECONDS), 'Cache-Control': 'no-store' });
    const fetchMode = req.get('sec-fetch-mode');
    const pageLoad = fetchMode ? fetchMode === 'navigate' : (req.get('accept') ?? '').includes('text/html');
    if (pageLoad) {
      // No script, no inline style (it renders under this BFF's CSP); the reload is a meta refresh.
      res.type('html').send(
        `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">` +
          `<meta http-equiv="refresh" content="${RETRY_AFTER_SECONDS}"><title>Temporarily unavailable</title></head><body>` +
          `<h1>Temporarily unavailable</h1><p>A service this page depends on is not answering. ` +
          `This page tries again by itself in ${RETRY_AFTER_SECONDS} seconds.</p></body></html>`,
      );
      return;
    }
    res.type('application/problem+json').send(JSON.stringify({
      type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
      title: 'Temporarily unavailable',
      status: 503,
      detail: `A service this request depends on is not answering. Try again in ${RETRY_AFTER_SECONDS} seconds.`,
    }));
  };
}