'use client';

import { Coins } from 'lucide-react';
import { unstable_isUnrecognizedActionError } from 'next/navigation';
import { useEffect } from 'react';
import { Badge } from './ui/feedback';
import { shellOrigin } from '../../lib/shell-origin';

export interface WorkspaceContextItem {
  title: string;
  value: string | number | boolean;
}

export interface WorkspaceContext {
  persistentContext: WorkspaceContextItem[];
  currentContext: WorkspaceContextItem[];
  retainedContext: WorkspaceContextItem[];
}

const EMPTY: WorkspaceContext = { persistentContext: [], currentContext: [], retainedContext: [] };

let latestWorkspaceContext: WorkspaceContext = EMPTY;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The workspace context, as in the other MFEs: the customer is the root (persistent) and
// │ the Digital Gold record the current item. Titles match theirs ("Customer Name",
// │ "Customer Number"), so the Shell keeps one customer across Customer Onboarding,
// │ Payments and Partner Products. Sent to the Shell's origin only.
// └──────────────────────────────────────────────────────────────────────────────────────
function publishWorkspaceContext(context: WorkspaceContext) {
  latestWorkspaceContext = context;
  if (window.parent && window.parent !== window) {
    window.parent.postMessage({ type: 'BSS_CONTEXT_UPDATE', context }, shellOrigin());
  }
}

const sameTitle = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * Tell the Shell which customer (root) and record (current) the staff member is on. The same
 * root keeps the retained context; another customer starts afresh.
 */
export function publishSelection(root: WorkspaceContextItem[], current: WorkspaceContextItem[]) {
  const previous = latestWorkspaceContext;
  const common = root.filter(item => previous.persistentContext.some(p => sameTitle(p.title, item.title)));
  const sameRoot = common.length > 0 && common.every(item =>
    previous.persistentContext.some(p => sameTitle(p.title, item.title) && String(p.value) === String(item.value)));

  if (!sameRoot) {
    publishWorkspaceContext({ persistentContext: root, currentContext: current, retainedContext: [] });
    return;
  }

  const persistentContext = [
    ...previous.persistentContext.filter(p => !root.some(item => sameTitle(item.title, p.title))),
    ...root,
  ];
  const retainedContext = previous.retainedContext.filter(r => !current.some(item => sameTitle(item.title, r.title)));
  publishWorkspaceContext({ persistentContext, currentContext: current, retainedContext });
}

/** On leaving: the current record moves to "retained", so the next page still shows it. */
function retainCurrentContext() {
  const current = latestWorkspaceContext.currentContext;
  if (current.length === 0) return;
  const titles = new Set(current.map(item => item.title.toLowerCase()));
  publishWorkspaceContext({
    persistentContext: latestWorkspaceContext.persistentContext,
    currentContext: [],
    retainedContext: [...latestWorkspaceContext.retainedContext.filter(item => !titles.has(item.title.toLowerCase())), ...current],
  });
}

/** How often, at most, this application tells the Shell that the person is active here. */
const ACTIVITY_REPORT_MS = 60_000;

/** After a redeploy, reload at most once a minute - a broken build must not reload in a loop. */
const NEW_BUILD_RELOAD_MS = 60_000;
const NEW_BUILD_RELOAD_KEY = 'ewp-new-build-reload';

// ┌─ ARCH + RESIL ───────────────────────────────────────────────────────────────────────
// │ Version skew: after a redeploy, a tab opened on the OLD build still calls server
// │ actions by their old ids, which the new build no longer has ("Failed to find Server
// │ Action", every poll). Such a call can never succeed, so reload the page once to load
// │ the new build. Only that error triggers it - every other failure is the page's own.
// │ OBS: nothing else is needed server-side; the reload ends the log noise as well.
// └──────────────────────────────────────────────────────────────────────────────────────
function useReloadOnNewBuild() {
  useEffect(() => {
    const onRejection = (event: PromiseRejectionEvent) => {
      if (!unstable_isUnrecognizedActionError(event.reason)) return;
      event.preventDefault();
      try {
        const last = Number(window.sessionStorage.getItem(NEW_BUILD_RELOAD_KEY) ?? 0);
        if (Date.now() - last < NEW_BUILD_RELOAD_MS) return;
        window.sessionStorage.setItem(NEW_BUILD_RELOAD_KEY, String(Date.now()));
      } catch {
        // No session storage (private window, blocked site data): still reload - the browser's
        // own back-off is then the only guard, which is acceptable for a rare redeploy.
      }
      window.location.reload();
    };
    window.addEventListener('unhandledrejection', onRejection);
    return () => window.removeEventListener('unhandledrejection', onRejection);
  }, []);
}

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
      parentWindow.postMessage({ type: 'BSS_MFE_ACTIVITY' }, shellOrigin());
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
 * The Shell-MFE protocol, as in the other MFEs: announce readiness (BSS_MFE_READY), take the
 * Shell's workspace context (BSS_CONTEXT_HANDOFF), publish the customer (publishSelection) and
 * answer the Shell's navigation requests. A Form VII or slip being entered is not saved anywhere until it is recorded -
 * leaving the page only drops what the staff member typed, the paper form is still in their hand - so
 * navigation is always allowed at once (no 30-second fail-open wait).
 */
export function MfeShell({ title, subtitle = 'Partner Products', children }: { title: string; subtitle?: string; children: React.ReactNode }) {

  useReportActivityToShell();
  useReloadOnNewBuild();
  useEffect(() => {
    const parentWindow = window.parent;
    if (!parentWindow || parentWindow === window) return;

    const handler = (event: MessageEvent) => {
      if (event.source !== parentWindow || event.origin !== shellOrigin()) return;
      // The Shell hands over the workspace it shows (e.g. the customer picked in Payments).
      if (event.data?.type === 'BSS_CONTEXT_HANDOFF') {
        latestWorkspaceContext = (event.data.context ?? EMPTY) as WorkspaceContext;
        return;
      }
      if (event.data?.type === 'BSS_NAVIGATION_REQUEST' && event.data.requestId) {
        retainCurrentContext();
        (event.source as Window).postMessage(
          { type: 'BSS_NAVIGATION_RESPONSE', requestId: event.data.requestId, allowed: true },
          event.origin,
        );
      }
    };

    window.addEventListener('message', handler);
    parentWindow.postMessage({ type: 'BSS_MFE_READY' }, shellOrigin());
    return () => window.removeEventListener('message', handler);
  }, []);

  return (
    <div className="min-h-screen bg-canvas">
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex max-w-[1440px] items-center justify-between gap-4 px-8 py-4">
          <div className="flex min-w-0 items-center gap-3">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-brand-800 text-white">
              <Coins className="size-[18px]" aria-hidden="true" />
            </span>
            <div className="min-w-0">
              <p className="text-xs font-medium uppercase tracking-wider text-ink-faint">{subtitle}</p>
              <h1 className="truncate font-display text-xl font-semibold text-ink">{title}</h1>
            </div>
          </div>
          <Badge tone="brand" dot>Partner Bank</Badge>
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