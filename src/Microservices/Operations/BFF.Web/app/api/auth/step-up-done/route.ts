import { NextRequest, NextResponse } from 'next/server';
import { config } from '@/lib/server/config';

export const dynamic = 'force-dynamic';

/**
 * The end of a step-up. In the Shell's overlay it goes on to the Shell's OWN fixed page (configured
 * Shell origin - never an address from the request), which the Shell recognises; in a window, the
 * page that opened it sees this address and closes it.
 */
export function GET(request: NextRequest) {
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ If the step-up ran in the Shell's overlay, go on to the Shell's own FIXED done page
  // │ (configured origin - never an address from the request: no open redirect). A forged
  // │ "done" gains nothing: the change is retried once and the API checks the fresh MFA.
  // └──────────────────────────────────────────────────────────────────────────────────────
  if (request.nextUrl.searchParams.get('in') === 'shell') {
    return NextResponse.redirect(`${new URL(config.shellOrigin).origin}/step-up-done.html`, { headers: { 'Cache-Control': 'no-store' } });
  }
  return new NextResponse(
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Confirmed</title></head>' +
    '<body><p>Confirmed. You can close this window; the change continues.</p></body></html>',
    { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } },
  );
}