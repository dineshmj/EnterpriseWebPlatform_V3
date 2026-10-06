'use client';

import { UserRoundCheck } from 'lucide-react';
import { useEffect, useState } from 'react';
import { ConfirmDialog } from './ui/confirm-dialog';
import { Badge } from './ui/feedback';

/**
 * Whether the currently displayed MFE page contains data that would be lost
 * if the Shell replaces the iframe URL.
 *
 * This is deliberately module-level so page/form components can update the
 * navigation guard without introducing React context solely for the
 * Shell/MFE protocol.
 */
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

const EMPTY_WORKSPACE_CONTEXT: WorkspaceContext = {
  persistentContext: [],
  currentContext: [],
  retainedContext: [],
};

// The MFE keeps the latest context so that it can perform the protocol-level
// transition of current -> retained when the user leaves the MFE. The MFE is
// still responsible for business meaning; this operation does not interpret
// any context item.
let latestWorkspaceContext: WorkspaceContext = EMPTY_WORKSPACE_CONTEXT;

// The only origin allowed to embed this MFE and exchange protocol messages with
// it. A static allow-list (set at build time), never document.referrer and
// never '*': a page that frames the MFE must not become its "trusted parent".
const SHELL_ORIGIN =
  process.env.NEXT_PUBLIC_SHELL_ORIGIN ?? 'https://shell.dev.localhost:46367';

function getParentOrigin(): string {
  return SHELL_ORIGIN;
}

/**
 * Publish the complete context owned by this MFE to the embedding Shell.
 * The Shell treats this payload as opaque and replaces its stored context.
 * Callers are responsible for supplying a complete, valid snapshot.
 */
export function publishWorkspaceContext(context: WorkspaceContext) {
  latestWorkspaceContext = context;

  window.parent?.postMessage(
    {
      type: 'BSS_CONTEXT_UPDATE',
      context,
    },
    getParentOrigin(),
  );
}

/** The workspace context this MFE last published or received from the Shell. */
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

/**
 * Change the persistent/root business scope. A root change invalidates all
 * subordinate context, so currentContext and retainedContext are reset.
 */
export function replacePersistentContext(
  persistentContext: WorkspaceContextItem[],
) {
  const nextContext: WorkspaceContext = {
    persistentContext,
    currentContext: [],
    retainedContext: [],
  };

  publishWorkspaceContext(nextContext);
}

/**
 * Move the current context to retained context without interpreting it.
 * Entries with the same title are replaced by the newly current entries;
 * title is the current protocol's identity convention until a stable key is
 * introduced.
 */
function retainCurrentContext(): WorkspaceContext {
  const current = latestWorkspaceContext.currentContext;

  if (current.length === 0) {
    return latestWorkspaceContext;
  }

  const currentTitles = new Set(
    current.map(item => item.title.toLowerCase()),
  );

  const retainedWithoutCurrentDuplicates =
    latestWorkspaceContext.retainedContext.filter(
      item => !currentTitles.has(item.title.toLowerCase()),
    );

  const nextContext: WorkspaceContext = {
    persistentContext: latestWorkspaceContext.persistentContext,
    currentContext: [],
    retainedContext: [
      ...retainedWithoutCurrentDuplicates,
      ...current,
    ],
  };

  publishWorkspaceContext(nextContext);
  return nextContext;
}

interface PendingNavigationRequest {
  requestId: string;
  source: Window;
  origin: string;
}

/** A notification the Shell relays to the MFE (its own copy of what the bell received). */
export interface ShellNotification {
  id: number;
  category: 'PROGRESS' | 'NEW_WORK' | string;
  title: string;
  body: string;
  target?: { mfe?: string; page?: string; recordId?: number } | null;
}

/**
 * Calls the listener for every notification the Shell relays to this MFE - e.g. a work
 * queue reloads when new work for it arrives. The Shell only relays; the page decides.
 */
export function useShellNotifications(listener: (notification: ShellNotification) => void) {
  useEffect(() => {
    const handler = (event: Event) => {
      const notification = (event as CustomEvent<ShellNotification>).detail;
      if (notification) listener(notification);
    };
    window.addEventListener('bss-notification', handler);
    return () => window.removeEventListener('bss-notification', handler);
  }, [listener]);
}

export function MfeShell({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: React.ReactNode;
}) {
  const [pendingNavigation, setPendingNavigation] =
    useState<PendingNavigationRequest | null>(null);

  useEffect(() => {
    // Messages are sent only to, and accepted only from, the configured Shell origin.
    const parentOrigin = getParentOrigin();

    const parentWindow = window.parent;

    const handler = (event: MessageEvent) => {
      // Only accept protocol messages from the embedding Shell.
      if (event.source !== parentWindow) return;
      if (event.origin !== parentOrigin) return;

      if (event.data?.type === 'BSS_CONTEXT_HANDOFF') {
        latestWorkspaceContext =
          (event.data.context ?? EMPTY_WORKSPACE_CONTEXT) as WorkspaceContext;

        // The Shell owns no business interpretation of this payload.
        // MFE components may consume it later through the custom event.
        window.dispatchEvent(
          new CustomEvent('bss-context-handoff', {
            detail: event.data.context,
          }),
        );
        return;
      }

      // A notification relayed by the Shell (it holds the only live connection).
      if (event.data?.type === 'BSS_NOTIFICATION') {
        window.dispatchEvent(
          new CustomEvent('bss-notification', { detail: event.data.notification }),
        );
        return;
      }

      if (event.data?.type === 'BSS_NAVIGATION_REQUEST') {
        const { requestId } = event.data;
        if (!requestId) return;

        const source = event.source as Window;

        if (!hasUnsavedChanges) {
          retainCurrentContext();

          source.postMessage(
            {
              type: 'BSS_NAVIGATION_RESPONSE',
              requestId,
              allowed: true,
            },
            event.origin,
          );
          return;
        }

        // Keep the V2 behavior: the MFE, not the generic Shell, owns the
        // user-facing unsaved-changes decision.
        setPendingNavigation({
          requestId,
          source,
          origin: event.origin,
        });
      }
    };

    window.addEventListener('message', handler);

    // Register the listener before announcing readiness. The Shell can
    // respond immediately with BSS_CONTEXT_HANDOFF, so the listener must
    // already be active to avoid a race.
    parentWindow.postMessage(
      { type: 'BSS_MFE_READY' },
      parentOrigin,
    );

    return () => {
      window.removeEventListener('message', handler);
    };
  }, []);

  const respondToNavigation = (allowed: boolean) => {
    if (!pendingNavigation) return;

    if (allowed) {
      // Publish the context transition before allowing the Shell to replace
      // the iframe, so the Shell has the retained context ready for the next
      // MFE's BSS_CONTEXT_HANDOFF.
      retainCurrentContext();

      // The current page is about to be replaced. Clear the flag so this
      // MFE instance no longer considers its state dirty.
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
          <div className="mx-auto flex max-w-[1440px] items-center justify-between gap-4 px-8 py-4">
            <div className="flex min-w-0 items-center gap-3">
              <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-brand-800 text-white">
                <UserRoundCheck className="size-[18px]" aria-hidden="true" />
              </span>
              <div className="min-w-0">
                <p className="text-xs font-medium uppercase tracking-wider text-ink-faint">{subtitle}</p>
                <h1 className="truncate font-display text-xl font-semibold text-ink">{title}</h1>
              </div>
            </div>
            <Badge tone="brand" dot>Customer Onboarding</Badge>
          </div>
        </header>

        <main className="mx-auto max-w-[1440px] px-8 py-6">{children}</main>
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