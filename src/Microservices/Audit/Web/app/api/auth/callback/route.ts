import { NextRequest, NextResponse } from 'next/server';
import { AUTH_STATE_COOKIE, config, SESSION_COOKIE, SESSION_IDLE_MINUTES } from '@/lib/server/config';
import { oidcClient, PendingSignIn, toSession } from '@/lib/server/oidc';
import { createSession, decrypt, removeExpiredSessions } from '@/lib/server/session-store';

export const dynamic = 'force-dynamic';

/** The IDP's answer: validates state, nonce and PKCE (openid-client), then starts the session. */
export async function GET(request: NextRequest) {
  const sealed = request.cookies.get(AUTH_STATE_COOKIE)?.value;
  let pending: PendingSignIn;
  try {
    pending = JSON.parse(decrypt(sealed ?? '')) as PendingSignIn;
  } catch {
    return signedOut('The sign-in took too long or was not started here. Open the Audit Trail from the menu again.');
  }

  // Silent sign-in refused (no SSO session at the IDP): the person must sign in to the Shell.
  if (request.nextUrl.searchParams.get('error')) {
    return signedOut('Your sign-in has ended. Sign in to the platform again, then open the Audit Trail.');
  }

  const client = await oidcClient();
  const tokens = await client.callback(
    config.callbackUrl,
    client.callbackParams(request.nextUrl.toString()),
    { state: pending.state, nonce: pending.nonce, code_verifier: pending.codeVerifier },
  );

  const cookieValue = await createSession(toSession(tokens));
  await removeExpiredSessions();

  const response = NextResponse.redirect(new URL(pending.returnUrl, config.publicOrigin));
  response.cookies.set(SESSION_COOKIE, cookieValue, {
    httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: SESSION_IDLE_MINUTES * 60,
  });
  response.cookies.delete(AUTH_STATE_COOKIE);
  return response;
}

function signedOut(message: string) {
  const response = new NextResponse(
    `<!doctype html><html lang="en"><body style="font-family:system-ui;padding:2rem;color:#334155"><p>${message}</p></body></html>`,
    { status: 401, headers: { 'Content-Type': 'text/html; charset=utf-8' } },
  );
  response.cookies.delete(AUTH_STATE_COOKIE);
  return response;
}