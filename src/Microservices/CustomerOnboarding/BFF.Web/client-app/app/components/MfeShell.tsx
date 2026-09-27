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
    // When the MFE is loaded inside the Shell, document.referrer contains
    // the embedding Shell URL. Using its origin keeps postMessage scoped to
    // the actual Shell rather than using a wildcard.
    let parentOrigin = '*';

    try {
      if (document.referrer) {
        parentOrigin = new URL(document.referrer).origin;
      }
    } catch {
      parentOrigin = '*';
    }

    const parentWindow = window.parent;

    const handler = (event: MessageEvent) => {
      // Only accept protocol messages from the embedding parent.
      if (event.source !== parentWindow) return;
      if (parentOrigin !== '*' && event.origin !== parentOrigin) return;

      if (event.data?.type === 'BSS_CONTEXT_HANDOFF') {
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

    // Register the listener before announcing readiness. The Shell responds
    // to BSS_MFE_READY immediately with BSS_CONTEXT_HANDOFF, so announcing
    // readiness first creates a race in which the handoff can be missed.
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

    pendingNavigation.source.postMessage(
      {
        type: 'BSS_NAVIGATION_RESPONSE',
        requestId: pendingNavigation.requestId,
        allowed,
      },
      pendingNavigation.origin,
    );

    if (allowed) {
      // The current page is about to be replaced. Clear the flag so this
      // MFE instance no longer considers its state dirty.
      hasUnsavedChanges = false;
    }

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
