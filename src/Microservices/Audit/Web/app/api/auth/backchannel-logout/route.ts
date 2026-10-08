import { NextRequest, NextResponse } from 'next/server';
import { createRemoteJWKSet, jwtVerify } from 'jose';
import { config } from '@/lib/server/config';
import { deleteSessionsOf } from '@/lib/server/session-store';

export const dynamic = 'force-dynamic';

let jwks: ReturnType<typeof createRemoteJWKSet> | undefined;

/**
 * Back-channel logout (server to server): the IDP posts a signed logout token. It must be
 * issued by the IDP for this client, carry the back-channel logout event and no nonce; then
 * every session of that IDP session (sid) - or of that person (sub) - ends.
 */
export async function POST(request: NextRequest) {
  const form = await request.formData();
  const logoutToken = form.get('logout_token');
  if (typeof logoutToken !== 'string') return new NextResponse(null, { status: 400 });

  jwks ??= createRemoteJWKSet(new URL(`${config.authority}/.well-known/openid-configuration/jwks`));
  try {
    const { payload } = await jwtVerify(logoutToken, jwks, { issuer: config.authority, audience: config.clientId });
    const events = payload.events as Record<string, unknown> | undefined;
    if (!events?.['http://schemas.openid.net/event/backchannel-logout'] || payload.nonce !== undefined) {
      return new NextResponse(null, { status: 400 });
    }
    await deleteSessionsOf(
      typeof payload.sid === 'string' ? payload.sid : undefined,
      typeof payload.sub === 'string' ? payload.sub : undefined,
    );
    return new NextResponse(null, { status: 200, headers: { 'Cache-Control': 'no-store' } });
  } catch {
    return new NextResponse(null, { status: 400 });
  }
}