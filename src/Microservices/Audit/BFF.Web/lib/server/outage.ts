import { headers } from 'next/headers';

// ┌─ :: RESILIENCE :: A database outage answered quickly and plainly ──────────────────────────────
// │ Why : The session lives in PostgreSQL. While it is unreachable nothing signed-in can be
// │       served - but a person must not get a white frame and then Next.js's bare error page.
// │ How : The session pool gives up after 5 s (session-store.ts). Then: a route handler (the
// │       Shell's frame, sign-in, sign-out) answers 503 + Retry-After - a page that tries again by
// │       itself for a page load, Problem Details for a call - the same answer as the .NET hosts
// │       (Common.WebUtilities/Resilience/DatabaseOutage.cs); a server action returns its usual
// │       error, "temporarily unavailable". Only "cannot reach the database" is treated so; every
// │       other error keeps its normal handling.
// │ Ref : doc/Containers.md | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
export const RETRY_AFTER_SECONDS = 15;
export const OUTAGE_MESSAGE = 'EWP is temporarily unavailable. Try again in a moment.';

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

/**
 * Wraps a route handler: a database outage becomes 503 + Retry-After; anything else (including
 * Next.js's own redirect / not-found signals) is passed on unchanged.
 */
export function withDatabaseOutageAnswer<A extends unknown[]>(handler: (...args: A) => Promise<Response>) {
  return async (...args: A): Promise<Response> => {
    try {
      return await handler(...args);
    } catch (error) {
      if (!isDatabaseUnavailable(error)) throw error;
      console.warn(`Database unreachable: answered 503 (${(error as Error).message ?? error})`);
      const request = await headers();
      return outageResponse(request.get('sec-fetch-mode'), request.get('accept'));
    }
  };
}

function outageResponse(fetchMode: string | null, accept: string | null): Response {
  const common = { 'Retry-After': String(RETRY_AFTER_SECONDS), 'Cache-Control': 'no-store' };
  const pageLoad = fetchMode ? fetchMode === 'navigate' : (accept ?? '').includes('text/html');
  if (pageLoad) {
    // No script, no inline style (it renders under this app's CSP); the reload is a meta refresh.
    return new Response(
      `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">` +
        `<meta http-equiv="refresh" content="${RETRY_AFTER_SECONDS}"><title>Temporarily unavailable</title></head><body>` +
        `<h1>Temporarily unavailable</h1><p>A service this page depends on is not answering. ` +
        `This page tries again by itself in ${RETRY_AFTER_SECONDS} seconds.</p></body></html>`,
      { status: 503, headers: { ...common, 'Content-Type': 'text/html; charset=utf-8' } },
    );
  }
  return Response.json(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.6.4',
      title: 'Temporarily unavailable',
      status: 503,
      detail: `A service this request depends on is not answering. Try again in ${RETRY_AFTER_SECONDS} seconds.`,
    },
    { status: 503, headers: { ...common, 'Content-Type': 'application/problem+json' } },
  );
}