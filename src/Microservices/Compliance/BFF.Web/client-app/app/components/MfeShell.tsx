'use client';

import { Scale } from 'lucide-react';
import { useEffect, useState } from 'react';
import { cn } from './ui/cn';
import { ConfirmDialog } from './ui/confirm-dialog';
import { Badge } from './ui/feedback';

let hasUnsavedChanges = false;

export function setUnsavedChanges(value: boolean) {
  hasUnsavedChanges = value;
}

export function getUnsavedChanges() {
  return hasUnsavedChanges;
}

export interface WorkspaceContextItem {
  title: string;
  value: string | number | boolean;
}

export interface WorkspaceContext {
  persistentContext: WorkspaceContextItem[];
  currentContext: WorkspaceContextItem[];
  retainedContext: WorkspaceContextItem[];
}

const EMPTY: WorkspaceContext = {
  persistentContext: [],
  currentContext: [],
  retainedContext: [],
};

let latestWorkspaceContext: WorkspaceContext = EMPTY;

// The only origin allowed to embed this MFE and exchange protocol messages with
// it. A static allow-list (set at build time), never document.referrer and
// never '*': a page that frames the MFE must not become its "trusted parent".
const SHELL_ORIGIN =
  process.env.NEXT_PUBLIC_SHELL_ORIGIN ?? 'https://shell.dev.localhost:44367';

function parentOrigin(): string {
  return SHELL_ORIGIN;
}

export function publishWorkspaceContext(context: WorkspaceContext) {
  latestWorkspaceContext = context;
  window.parent?.postMessage({ type: 'BSS_CONTEXT_UPDATE', context }, parentOrigin());
}

/** The latest workspace context: the Shell's hand-over, or what this MFE published since. */
export function getWorkspaceContext(): WorkspaceContext {
  return latestWorkspaceContext;
}

const sameTitle = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/**
 * Tell the Shell which record the user has picked.
 *
 * `root` identifies the business scope (the customer); `current` describes the
 * picked record. If the root is the one already shown (every title the two
 * have in common carries the same value), the existing root is kept and
 * enriched, and retained context survives. A different root invalidates all
 * subordinate context, so retained context is discarded.
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

function retainCurrentContext(): WorkspaceContext {
  const current = latestWorkspaceContext.currentContext;
  if (current.length === 0) return latestWorkspaceContext;

  const currentTitles = new Set(current.map(item => item.title.toLowerCase()));
  const retained = latestWorkspaceContext.retainedContext.filter(
    item => !currentTitles.has(item.title.toLowerCase()),
  );

  const nextContext: WorkspaceContext = {
    persistentContext: latestWorkspaceContext.persistentContext,
    currentContext: [],
    retainedContext: [...retained, ...current],
  };

  publishWorkspaceContext(nextContext);
  return nextContext;
}

interface PendingNavigationRequest {
  requestId: string;
  source: Window;
  origin: string;
}

export function MfeShell({
  title = 'Compliance review',
  subtitle = 'Compliance',
  wide = false,
  children,
}: {
  title?: string;
  subtitle?: string;
  /** Review screens use more of very wide monitors (evidence + decision side by side). */
  wide?: boolean;
  children: React.ReactNode;
}) {
  const [pendingNavigation, setPendingNavigation] =
    useState<PendingNavigationRequest | null>(null);

  useEffect(() => {
    const origin = parentOrigin();
    const parentWindow = window.parent;

    const handler = (event: MessageEvent) => {
      if (event.source !== parentWindow) return;
      if (event.origin !== origin) return;

      if (event.data?.type === 'BSS_CONTEXT_HANDOFF') {
        const nextContext =
          (event.data.context ?? EMPTY) as WorkspaceContext;
        latestWorkspaceContext = nextContext;
        window.dispatchEvent(
          new CustomEvent('bss-context-handoff', { detail: nextContext }),
        );
        return;
      }

      if (event.data?.type === 'BSS_NAVIGATION_REQUEST') {
        const { requestId } = event.data;
        if (!requestId) return;

        const source = event.source as Window;

        if (!hasUnsavedChanges) {
          // The Shell waits for this response before replacing the iframe.
          // Returning it immediately avoids the Shell's 30-second fail-open
          // timeout for read-only views.
          retainCurrentContext();
          source.postMessage(
            { type: 'BSS_NAVIGATION_RESPONSE', requestId, allowed: true },
            event.origin,
          );
          return;
        }

        setPendingNavigation({
          requestId,
          source,
          origin: event.origin,
        });
      }
    };

    window.addEventListener('message', handler);

    // The listener must be registered before BSS_MFE_READY because the Shell
    // can immediately send BSS_CONTEXT_HANDOFF in response.
    parentWindow.postMessage({ type: 'BSS_MFE_READY' }, origin);

    return () => window.removeEventListener('message', handler);
  }, []);

  const respondToNavigation = (allowed: boolean) => {
    if (!pendingNavigation) return;

    if (allowed) {
      retainCurrentContext();
      hasUnsavedChanges = false;
    }

    pendingNavigation.source.postMessage(
      {
        type: 'BSS_NAVIGATION_RESPONSE',
        requestId: pendingNavigation.requestId,
        allowed,
      },
      pendingNavigation.origin,
    );

    setPendingNavigation(null);
  };

  return (
    <>
      <div className="min-h-screen bg-canvas">
        <header className="border-b border-line bg-surface">
          <div className={cn('mx-auto flex items-center justify-between gap-4 px-8 py-4', wide ? 'max-w-[1920px]' : 'max-w-[1440px]')}>
            <div className="flex min-w-0 items-center gap-3">
              <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-brand-800 text-white">
                <Scale className="size-[18px]" aria-hidden="true" />
              </span>
              <div className="min-w-0">
                <p className="text-xs font-medium uppercase tracking-wider text-ink-faint">{subtitle}</p>
                <h1 className="truncate font-display text-xl font-semibold text-ink">{title}</h1>
              </div>
            </div>
            <Badge tone="brand" dot>Compliance</Badge>
          </div>
        </header>

        <main className={cn('mx-auto px-8 py-6', wide ? 'max-w-[1920px]' : 'max-w-[1440px]')}>{children}</main>
      </div>

      {pendingNavigation && (
        <ConfirmDialog
          title="Unsaved changes"
          cancelLabel="Stay on this page"
          confirmLabel="Leave without saving"
          onCancel={() => respondToNavigation(false)}
          onConfirm={() => respondToNavigation(true)}
        >
          You have unsaved changes on this page. If you leave now, they will be lost.
        </ConfirmDialog>
      )}
    </>
  );
}