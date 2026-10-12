import { NextRequest, NextResponse } from 'next/server';
import { AUTH_STATE_COOKIE, config, SESSION_COOKIE } from '@/lib/server/config';
import { authorizationRequest } from '@/lib/server/oidc';
import { safeReturnUrl } from '@/lib/server/return-url';
import { encrypt, readSession } from '@/lib/server/session-store';

export const dynamic = 'force-dynamic';

/**
 * Where the Shell points this MFE's frame (?returnUrl=<page>), like every other MFE: an existing
 * session goes straight to the page; otherwise a silent sign-in (prompt=none) against the IDP's
 * SSO session. The return URL is allow-listed.
 */
export async function GET(request: NextRequest) {
  const returnUrl = safeReturnUrl(request.nextUrl.searchParams.get('returnUrl'));

  if (await readSession(request.cookies.get(SESSION_COOKIE)?.value)) {
    return NextResponse.redirect(new URL(returnUrl, config.publicOrigin));
  }

  const { url, pending } = await authorizationRequest(returnUrl, true);
  const response = NextResponse.redirect(url);
  response.cookies.set(AUTH_STATE_COOKIE, encrypt(JSON.stringify(pending)), {
    httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 10 * 60,
  });
  return response;
}