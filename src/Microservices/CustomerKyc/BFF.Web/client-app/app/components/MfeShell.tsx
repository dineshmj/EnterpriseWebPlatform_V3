'use client';

import { useEffect, useState } from 'react';

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

export function MfeShell({ children }: { children: React.ReactNode }) {
  const [context, setContext] = useState<WorkspaceContext>(EMPTY);
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
        setContext(nextContext);
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
          // timeout for read-only KYC views.
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
      <main className="mfe" data-workspace-context={JSON.stringify(context)}>
        <header className="header">
          <div>
            <h1 className="title">Customer KYC</h1>
            <p className="subtitle">KYC Management · Review Work Queue</p>
          </div>
          <span className="badge">● Customer KYC MFE</span>
        </header>
        {children}
      </main>

      {pendingNavigation && (
        <div
          role="presentation"
          style={{
            position: 'fixed', inset: 0, zIndex: 9999, display: 'flex',
            alignItems: 'center', justifyContent: 'center',
            background: 'rgba(0, 0, 0, 0.42)',
          }}
        >
          <div
            role="alertdialog" aria-modal="true"
            aria-labelledby="bss-nav-guard-title"
            aria-describedby="bss-nav-guard-description"
            style={{
              width: 'min(460px, calc(100vw - 32px))', padding: '24px',
              borderRadius: '10px', background: '#fff',
              boxShadow: '0 20px 60px rgba(0, 0, 0, 0.25)', color: '#1f2937',
            }}
          >
            <h2 id="bss-nav-guard-title" style={{ margin: '0 0 10px', fontSize: '1.25rem' }}>
              Unsaved changes
            </h2>
            <p id="bss-nav-guard-description" style={{ margin: '0 0 20px', lineHeight: 1.5 }}>
              You have unsaved changes on this page. If you leave now, they will be lost.
            </p>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px' }}>
              <button type="button" onClick={() => respondToNavigation(false)} autoFocus
                style={{ padding: '9px 14px', borderRadius: '6px', border: '1px solid #d0d5dd', background: '#fff', cursor: 'pointer' }}>
                Stay on this page
              </button>
              <button type="button" onClick={() => respondToNavigation(true)}
                style={{ padding: '9px 14px', borderRadius: '6px', border: '1px solid #b42318', background: '#b42318', color: '#fff', cursor: 'pointer' }}>
                Leave without saving
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
