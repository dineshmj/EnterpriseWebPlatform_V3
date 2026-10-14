'use client';

import { Activity } from 'lucide-react';
import { useEffect } from 'react';
import { Badge } from './ui/feedback';

// The only origin allowed to embed this MFE and exchange protocol messages with it. A static
// allow-list (set at build time), never document.referrer and never '*'.
const SHELL_ORIGIN = process.env.NEXT_PUBLIC_SHELL_ORIGIN ?? 'https://shell.dev.localhost:46367';

/** How often, at most, this application tells the Shell that the person is active here. */
const ACTIVITY_REPORT_MS = 60_000;

// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ While the person works here, tell the Shell (at most once a minute, its origin only,
// │ no data) so the Shell keeps its OWN session alive - each BFF has its own session, and
// │ the Shell's would otherwise end while the person is busy in this application.
// └──────────────────────────────────────────────────────────────────────────────────────
function useReportActivityToShell() {
  useEffect(() => {
    const parentWindow = window.parent;
    if (!parentWindow || parentWindow === window) return;
    let lastReport = 0;
    const report = () => {
      const now = Date.now();
      if (now - lastReport < ACTIVITY_REPORT_MS) return;
      lastReport = now;
      parentWindow.postMessage({ type: 'BSS_MFE_ACTIVITY' }, SHELL_ORIGIN);
    };
    window.addEventListener('pointerdown', report, true);
    window.addEventListener('keydown', report, true);
    return () => {
      window.removeEventListener('pointerdown', report, true);
      window.removeEventListener('keydown', report, true);
    };
  }, []);
}

/**
 * The Shell-MFE protocol, as in the other MFEs: announce readiness (BSS_MFE_READY), and answer
 * the Shell's navigation requests. The operations screens hold no unsaved input - nothing can be lost - so
 * navigation is always allowed at once (no 30-second fail-open wait).
 */
export function MfeShell({ title, subtitle = 'Operations', children }: { title: string; subtitle?: string; children: React.ReactNode }) {

  useReportActivityToShell();
  useEffect(() => {
    const parentWindow = window.parent;
    if (!parentWindow || parentWindow === window) return;

    const handler = (event: MessageEvent) => {
      if (event.source !== parentWindow || event.origin !== SHELL_ORIGIN) return;
      if (event.data?.type === 'BSS_NAVIGATION_REQUEST' && event.data.requestId) {
        (event.source as Window).postMessage(
          { type: 'BSS_NAVIGATION_RESPONSE', requestId: event.data.requestId, allowed: true },
          event.origin,
        );
      }
    };

    window.addEventListener('message', handler);
    parentWindow.postMessage({ type: 'BSS_MFE_READY' }, SHELL_ORIGIN);
    return () => window.removeEventListener('message', handler);
  }, []);

  return (
    <div className="min-h-screen bg-canvas">
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex max-w-[1440px] items-center justify-between gap-4 px-8 py-4">
          <div className="flex min-w-0 items-center gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-brand-800 text-white">
              <Activity className="size-[18px]" aria-hidden="true" />
            </span>
            <div className="min-w-0">
              <p className="text-xs font-medium uppercase tracking-wider text-ink-faint">{subtitle}</p>
              <h1 className="truncate font-display text-xl font-semibold text-ink">{title}</h1>
            </div>
          </div>
          <Badge tone="brand" dot>Operations</Badge>
        </div>
      </header>
      <main className="mx-auto max-w-[1440px] px-8 py-6">{children}</main>
    </div>
  );
}

/** After a session ends, sign in again silently and come back to the same page. */
export function signInAgain() {
  window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}`;
}

/** Dates and amounts in the bank's locale (en-AU), whatever the browser's. */
export const formatTime = (value: string) =>
  new Date(value).toLocaleString('en-AU', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });

export const formatAmount = (amount: number | null, currency: string | null) =>
  amount === null ? '' : new Intl.NumberFormat('en-AU', { style: 'currency', currency: currency ?? 'AUD' }).format(amount);

export const personLabel = (person: { userId: string; lanId: string | null } | null) =>
  person ? person.lanId ?? `${person.userId.slice(0, 8)}…` : '';