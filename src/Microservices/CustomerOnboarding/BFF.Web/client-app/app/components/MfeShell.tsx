'use client';

import { useEffect, useState } from 'react';

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
  process.env.NEXT_PUBLIC_SHELL_ORIGIN ?? 'https://shell.dev.localhost:44367';

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
      <main className="mfe">
        <header className="header">
          <div>
            <h1 className="title">{title}</h1>
            <p className="subtitle">{subtitle}</p>
          </div>
          <span className="badge">● Customer Onboarding MFE</span>
        </header>

        {children}
      </main>

      {pendingNavigation && (
        <div
          role="presentation"
          style={{
            position: 'fixed',
            inset: 0,
            zIndex: 9999,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            background: 'rgba(0, 0, 0, 0.42)',
          }}
        >
          <div
            role="alertdialog"
            aria-modal="true"
            aria-labelledby="bss-nav-guard-title"
            aria-describedby="bss-nav-guard-description"
            style={{
              width: 'min(460px, calc(100vw - 32px))',
              padding: '24px',
              borderRadius: '10px',
              background: '#fff',
              boxShadow: '0 20px 60px rgba(0, 0, 0, 0.25)',
              color: '#1f2937',
            }}
          >
            <h2
              id="bss-nav-guard-title"
              style={{
                margin: '0 0 10px',
                fontSize: '1.25rem',
              }}
            >
              Unsaved changes
            </h2>

            <p
              id="bss-nav-guard-description"
              style={{
                margin: '0 0 20px',
                lineHeight: 1.5,
              }}
            >
              You have unsaved changes on this page. If you leave now, they
              will be lost.
            </p>

            <div
              style={{
                display: 'flex',
                justifyContent: 'flex-end',
                gap: '10px',
              }}
            >
              <button
                type="button"
                onClick={() => respondToNavigation(false)}
                autoFocus
                style={{
                  padding: '9px 14px',
                  borderRadius: '6px',
                  border: '1px solid #d0d5dd',
                  background: '#fff',
                  cursor: 'pointer',
                }}
              >
                Stay on this page
              </button>

              <button
                type="button"
                onClick={() => respondToNavigation(true)}
                style={{
                  padding: '9px 14px',
                  borderRadius: '6px',
                  border: '1px solid #b42318',
                  background: '#b42318',
                  color: '#fff',
                  cursor: 'pointer',
                }}
              >
                Leave without saving
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
