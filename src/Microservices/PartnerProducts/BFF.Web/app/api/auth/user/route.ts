import { NextRequest, NextResponse } from 'next/server';
import { SESSION_COOKIE } from '@/lib/server/config';
import { readSession } from '@/lib/server/session-store';
import { withOutageAnswer } from '@/lib/server/outage';

export const dynamic = 'force-dynamic';

/** Who is signed in - display only (LAN ID and name); never a token. */
async function handleGet(request: NextRequest) {
  const session = await readSession(request.cookies.get(SESSION_COOKIE)?.value);
  if (!session) return NextResponse.json({ signedIn: false }, { status: 401 });
  return NextResponse.json({ signedIn: true, lanId: session.lanId ?? null, name: session.name ?? null });
}

// RESIL: a dependency unavailable (database, IDP) -> 503 + Retry-After instead of an error page (outage.ts).
export const GET = withOutageAnswer(handleGet);