import { NextResponse } from 'next/server';
import { AUTH_STATE_COOKIE } from '@/lib/server/config';
import { authorizationRequest } from '@/lib/server/oidc';
import { STEP_UP_DONE_PATH } from '@/lib/server/return-url';
import { encrypt } from '@/lib/server/session-store';

export const dynamic = 'force-dynamic';

/**
 * Step-up for a risky change, opened by the page in a small window (the IDP's pages are never
 * framed): the IDP asks only for a fresh authenticator code, and the new session's tokens carry a
 * new auth_time - which the token exchange keeps for the Journey API. The window then lands on
 * step-up-done, which the page recognises and closes.
 */
export async function GET() {
  const { url, pending } = await authorizationRequest(STEP_UP_DONE_PATH, false, true);
  const response = NextResponse.redirect(url);
  response.cookies.set(AUTH_STATE_COOKIE, encrypt(JSON.stringify(pending)), {
    httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 10 * 60,
  });
  return response;
}