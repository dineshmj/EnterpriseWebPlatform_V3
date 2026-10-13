import { NextResponse } from 'next/server';

export const dynamic = 'force-dynamic';

/** The end of the step-up window: the page that opened it sees this address and closes it. */
export function GET() {
  return new NextResponse(
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Confirmed</title></head>' +
    '<body><p>Confirmed. You can close this window; the change continues.</p></body></html>',
    { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } },
  );
}