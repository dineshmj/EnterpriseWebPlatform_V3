import { NextRequest, NextResponse } from 'next/server';
import { AUTH_STATE_COOKIE } from '@/lib/server/config';
import { authorizationRequest } from '@/lib/server/oidc';
import { STEP_UP_DONE_PATH } from '@/lib/server/return-url';
import { encrypt } from '@/lib/server/session-store';
import { withOutageAnswer } from '@/lib/server/outage';

export const dynamic = 'force-dynamic';

// ┌─ :: SECURITY :: Step-up sign-in in the Shell's overlay (or a window) ──────────────────────────
// │ Why : A risky change inside the frame may need a fresh code; the IDP's sign-in pages are never
// │       framed - only its step-up code page, and only by the Shell.
// │ How : prompt=login + acr_values=mfa: the IDP asks only for the code; it returns to the fixed
// │       /api/auth/step-up-done ("?in=shell" in the Shell's overlay), and the screen retries once.
// │ Ref : Features 2.1.5 | RFC 9470 | Threat-Model R-13
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * Step-up for a risky change: opened by the Shell in its in-page overlay (in=shell), or in a small
 * window when the page runs outside the Shell. The IDP asks only for a fresh authenticator code, and
 * the new session's tokens carry a new auth_time - which the token exchange keeps for the Journey API.
 */
async function handleGet(request: NextRequest) {
  const done = request.nextUrl.searchParams.get('in') === 'shell' ? `${STEP_UP_DONE_PATH}?in=shell` : STEP_UP_DONE_PATH;
  const { url, pending } = await authorizationRequest(done, false, true);
  const response = NextResponse.redirect(url);
  response.cookies.set(AUTH_STATE_COOKIE, encrypt(JSON.stringify(pending)), {
    httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 10 * 60,
  });
  return response;
}

// RESIL: a dependency unavailable (database, IDP) -> 503 + Retry-After instead of an error page (outage.ts).
export const GET = withOutageAnswer(handleGet);