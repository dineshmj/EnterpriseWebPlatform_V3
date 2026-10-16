import { NextRequest, NextResponse } from 'next/server';
import { SESSION_COOKIE } from '@/lib/server/config';
import { readSession } from '@/lib/server/session-store';

export const dynamic = 'force-dynamic';

/** Who is signed in - display only (LAN ID and name); never a token. */
export async function GET(request: NextRequest) {
  const session = await readSession(request.cookies.get(SESSION_COOKIE)?.value);
  if (!session) return NextResponse.json({ signedIn: false }, { status: 401 });
  return NextResponse.json({ signedIn: true, lanId: session.lanId ?? null, name: session.name ?? null });
}