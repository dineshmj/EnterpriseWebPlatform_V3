import { NextRequest, NextResponse } from 'next/server';
import { config, SESSION_COOKIE } from '@/lib/server/config';
import { deleteSession, deleteSessionsOf } from '@/lib/server/session-store';
import { withOutageAnswer } from '@/lib/server/outage';

export const dynamic = 'force-dynamic';

/**
 * Front-channel logout: the IDP's signed-out page loads this in a hidden iframe. Ends the
 * session of this browser, and every session of the IDP session (sid) when the issuer matches.
 */
async function handleGet(request: NextRequest) {
  const cookieValue = request.cookies.get(SESSION_COOKIE)?.value;
  if (cookieValue) await deleteSession(cookieValue);

  const iss = request.nextUrl.searchParams.get('iss');
  const sid = request.nextUrl.searchParams.get('sid');
  if (sid && iss?.replace(/\/$/, '') === config.authority) await deleteSessionsOf(sid, undefined);

  const response = new NextResponse('<!doctype html><html><body></body></html>', {
    headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' },
  });
  response.cookies.delete(SESSION_COOKIE);
  return response;
}

// RESIL: a dependency unavailable (database, IDP) -> 503 + Retry-After instead of an error page (outage.ts).
export const GET = withOutageAnswer(handleGet);