import { NextRequest, NextResponse } from 'next/server';
import { config as settings } from './lib/server/config';

// ┌─ :: SECURITY :: Audit web: security headers on every response ─────────────────────────────────
// │ Why : The same browser threats as every MFE: injected script, framing by another site, MIME
// │       sniffing.
// │ How : A per-request nonce CSP (no 'unsafe-inline' scripts), frame-ancestors the Shell and the
// │       IDP only, nosniff and a strict referrer policy.
// │ Ref : Features 2.3.1, 2.3.2 | CSP Level 3 | OWASP Clickjacking Defense | CWE-1021
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * Security headers for every response, with a strict Content-Security-Policy: scripts only
 * from this app and only with this request's nonce ('strict-dynamic' lets Next.js's own
 * chunks load); framed only by the Shell (and the IDP, which frames the front-channel logout).
 * Next.js reads the nonce from the request's CSP header and puts it on its scripts.
 */
export function proxy(request: NextRequest) {
  const nonce = Buffer.from(crypto.randomUUID()).toString('base64');
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ If the Shell or IDP origin is not configured, fail the request - never fall back to
  // │ a development origin, which would let the wrong site frame this app.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const shell = settings.shellOrigin;
  const idp = settings.authority;

  const csp = [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'`,
    // Styles from this app's files, plus inline ones only with this request's nonce. The dev
    // server injects its styles from script without a nonce, so it alone allows inline styles.
    process.env.NODE_ENV === 'production' ? `style-src 'self' 'nonce-${nonce}'` : "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data:",
    "font-src 'self'",
    "connect-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    `frame-ancestors ${new URL(shell).origin} ${new URL(idp).origin}`,
  ].join('; ');

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set('x-nonce', nonce);
  requestHeaders.set('Content-Security-Policy', csp);

  const response = NextResponse.next({ request: { headers: requestHeaders } });
  response.headers.set('Content-Security-Policy', csp);
  response.headers.set('X-Content-Type-Options', 'nosniff');
  response.headers.set('Referrer-Policy', 'strict-origin-when-cross-origin');
  return response;
}

export const config = {
  matcher: [{ source: '/((?!_next/static|_next/image|favicon.ico).*)' }],
};