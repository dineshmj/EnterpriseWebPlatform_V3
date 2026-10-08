import { NextRequest, NextResponse } from 'next/server';

/**
 * Security headers for every response, with a strict Content-Security-Policy: scripts only
 * from this app and only with this request's nonce ('strict-dynamic' lets Next.js's own
 * chunks load); framed only by the Shell (and the IDP, which frames the front-channel logout).
 * Next.js reads the nonce from the request's CSP header and puts it on its scripts.
 */
export function proxy(request: NextRequest) {
  const nonce = Buffer.from(crypto.randomUUID()).toString('base64');
  const shell = process.env.AUDIT_WEB_SHELL_ORIGIN ?? 'https://shell.dev.localhost:46367';
  const idp = process.env.AUDIT_WEB_IDP_AUTHORITY ?? 'https://idp.dev.localhost:46392';

  const csp = [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'`,
    // Inline styles as in the other MFEs (see the Blueprint's "Not yet").
    "style-src 'self' 'unsafe-inline'",
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