import { headers } from 'next/headers';

// ┌─ :: RESILIENCE :: An outage of a dependency answered quickly and plainly ──────────────────────
// │ Why : The session lives in PostgreSQL and every sign-in goes through the IDP. While one of them
// │       is unreachable nothing signed-in can be served - but a person must not get a white frame
// │       and then Next.js's bare error page.
// │ How : The session pool gives up after 5 s (session-store.ts). Then: a route handler (the
// │       Shell's frame, sign-in, sign-out) answers 503 + Retry-After - a page that tries again by
// │       itself for a page load, Problem Details for a call - the same answer as the .NET hosts
// │       (Common.WebUtilities/Resilience/DependencyOutage.cs); a server action returns its usual
// │       error, "temporarily unavailable". Only "a dependency is unavailable" is treated so - the
// │       database cannot be reached, or the IDP cannot be reached / times out / answers 502-504
// │       (e.g. its own database is down); every other error keeps its normal handling.
// │ Ref : doc/Containers.md | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
export const RETRY_AFTER_SECONDS = 15;
export const OUTAGE_MESSAGE = 'EWP is temporarily unavailable. Try again in a moment.';

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

/**
 * Wraps a route handler: a dependency's outage becomes 503 + Retry-After; anything else
 * (including Next.js's own redirect / not-found signals) is passed on unchanged.
 */
export function withOutageAnswer<A extends unknown[]>(handler: (...args: A) => Promise<Response>) {
  return async (...args: A): Promise<Response> => {
    try {
      return await handler(...args);
    } catch (error) {
      if (!isDependencyUnavailable(error)) throw error;
      console.warn(`Dependency unavailable: answered 503 (${(error as Error).message ?? error})`);
      const request = await headers();
      return outageResponse(request.get('sec-fetch-mode'), request.get('accept'));
    }
  };
}

function outageResponse(fetchMode: string | null, accept: string | null): Response {
  const common = { 'Retry-After': String(RETRY_AFTER_SECONDS), 'Cache-Control': 'no-store' };
  const pageLoad = fetchMode ? fetchMode === 'navigate' : (accept ?? '').includes('text/html');
  if (pageLoad) {
    // No script, no inline style (it renders under this app's CSP; the look comes from /ewp-outage.css,
    // served by server.mjs); the reload is a meta refresh.
    return new Response(
      `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">` +
        `<meta http-equiv="refresh" content="${RETRY_AFTER_SECONDS}"><title>Temporarily unavailable</title>` +
        `<link rel="stylesheet" href="/ewp-outage.css"></head><body><main>` +
        `<h1>Temporarily unavailable</h1><p>A service this page depends on is not answering. ` +
        `This page tries again by itself in ${RETRY_AFTER_SECONDS} seconds.</p></main></body></html>`,
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