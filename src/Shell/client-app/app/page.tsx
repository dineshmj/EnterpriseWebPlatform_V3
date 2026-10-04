'use client';

import { useState, useEffect, useRef } from 'react';
import { AuthGuard } from './components/AuthGuard';
import { UserProfile } from './components/UserProfile';
import { ApplicationWorkspace } from './components/ApplicationWorkspace';
import Image from 'next/image';
import { TopNavMenu } from './components/TopNavMenu';
import { useAuth } from './hooks/useAuth';
import { MenuResponse, MenuItem } from './types';
import { addVisitedMicroservice, setDiscoveredMicroservices } from './lib/auth-utils';
import { EMPTY_WORKSPACE_CONTEXT, type WorkspaceContext } from './workspace-context';
import styles from './page.module.css';

const NAVIGATION_RESPONSE_TIMEOUT_MS = 30_000;

export default function Home() {
  return <AuthGuard><HomeContent /></AuthGuard>;
}

/**
 * Ask the currently loaded MFE whether the page can be replaced.
 *
 * The Shell deliberately does not know what "unsaved" means. The MFE owns
 * that decision and returns only allowed=true/false.
 */
function requestNavigationPermission(
  iframe: HTMLIFrameElement,
  currentOrigin: string,
): Promise<boolean> {
  return new Promise((resolve) => {
    const requestId = crypto.randomUUID();
    let settled = false;

    const finish = (allowed: boolean) => {
      if (settled) return;
      settled = true;
      window.removeEventListener('message', handleResponse);
      resolve(allowed);
    };

    function handleResponse(event: MessageEvent) {
      if (event.origin !== currentOrigin) return;
      if (event.source !== iframe.contentWindow) return;
      if (event.data?.type !== 'BSS_NAVIGATION_RESPONSE') return;
      if (event.data.requestId !== requestId) return;

      finish(event.data.allowed !== false);
    }

    window.addEventListener('message', handleResponse);

    iframe.contentWindow?.postMessage(
      { type: 'BSS_NAVIGATION_REQUEST', requestId },
      currentOrigin,
    );

    // Preserve the V2 fail-open behavior: if an MFE does not answer,
    // navigation is not blocked indefinitely.
    window.setTimeout(() => finish(true), NAVIGATION_RESPONSE_TIMEOUT_MS);
  });
}

function HomeContent() {
  const { user } = useAuth(false);
  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);
  const [menuData, setMenuData] = useState<MenuResponse | null>(null);

  const [workspaceContext, setWorkspaceContext] =
    useState<WorkspaceContext>(EMPTY_WORKSPACE_CONTEXT);

  const currentFrameOriginRef = useRef<string | null>(null);

  // The Shell is only a transport/rendering host for workspace context.
  const currentContextRef = useRef<WorkspaceContext>(EMPTY_WORKSPACE_CONTEXT);

  useEffect(() => {
    function handleFrameMessage(event: MessageEvent) {
      const iframe = document.getElementById(
        'microservice-frame',
      ) as HTMLIFrameElement | null;

      if (!iframe || event.source !== iframe.contentWindow) return;

      if (event.data?.type === 'BSS_MFE_READY') {
        currentFrameOriginRef.current = event.origin;

        (event.source as Window).postMessage(
          {
            type: 'BSS_CONTEXT_HANDOFF',
            context: currentContextRef.current,
          },
          event.origin,
        );
        return;
      }

      if (event.data?.type === 'BSS_CONTEXT_UPDATE') {
        const nextContext =
          (event.data.context ?? EMPTY_WORKSPACE_CONTEXT) as WorkspaceContext;

        currentContextRef.current = nextContext;
        setWorkspaceContext(nextContext);
      }
    }

    window.addEventListener('message', handleFrameMessage);
    return () => window.removeEventListener('message', handleFrameMessage);
  }, []);

  const handleMenuItemClick = async (item: MenuItem) => {
    setError(null);
    setLoading(true);

    const iframe = document.getElementById(
      'microservice-frame',
    ) as HTMLIFrameElement | null;

    if (!iframe) {
      setError('Internal error: iframe not found.');
      setLoading(false);
      return;
    }

    const currentOrigin = currentFrameOriginRef.current;

    // Only the currently loaded MFE can answer this question.
    if (currentOrigin) {
      const canNavigate = await requestNavigationPermission(
        iframe,
        currentOrigin,
      );

      if (!canNavigate) {
        setLoading(false);
        return;
      }
    }

    if (!item.baseURL) {
      setError('Internal error: baseURL not found for the selected microservice.');
      setLoading(false);
      return;
    }

    addVisitedMicroservice(item.baseURL);

    // The new MFE will announce BSS_MFE_READY. At that point the Shell
    // hands it the latest opaque workspace context.
    currentFrameOriginRef.current = null;

    const silentLoginUrl =
      `${item.baseURL}/api/auth/silent-login` +
      `?returnUrl=${encodeURIComponent(item.urlRelativePath)}`;

    iframe.src = silentLoginUrl;
    iframe.onload = () => setLoading(false);
  };

  const loadMenu = async () => {
    try {
      console.log('>>> BSS Shell: loadMenu() called');

      const response = await fetch('/bff/api/Menu', {
        credentials: 'include',
      });

      console.log('>>> BSS Shell: Menu response:', response.status);

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }

      const data: MenuResponse = await response.json();
      setMenuData(data);
      setDiscoveredMicroservices(
        data.microservices.map((ms) => ms.baseURL),
      );
    } catch (e) {
      console.error('Failed to load menu:', e);
    }
  };

  useEffect(() => {
    loadMenu();
  }, []);

  if (!user) return null;

  return (
    <div className={styles.shell}>
      <header className={styles.brandHeader}>
        <div className={styles.brandMark}>
          <Image
            src="/res/BSS.png"
            alt="Banking Services System Logo"
            width={52}
            height={52}
            priority
          />
        </div>

        <div className={styles.brandText}>
          <h1 className={styles.brandTitle}>Banking Services System</h1>
          <p className={styles.brandSubtitle}>
            Enterprise application composition platform
          </p>
        </div>

        <UserProfile claims={user} />
      </header>

      <div className={styles.workspace}>
        {menuData && (
          <TopNavMenu
            microservices={menuData.microservices}
            handleMenuItemClick={handleMenuItemClick}
            loading={loading}
          />
        )}

        <main className={styles.main}>
          <div className={styles.workspaceBar}>
            <ApplicationWorkspace context={workspaceContext} />

            <span className={styles.connectionStatus}>
              <span className={styles.connectionDot} />
              Session active
            </span>
          </div>

          {error && (
            <div className={styles.error}>
              <strong>Error:</strong> {error}
            </div>
          )}

          <div className={styles.frameShell}>
            <iframe
              id="microservice-frame"
              className={styles.iframe}
              title="Microservice application workspace"
            />
          </div>
        </main>
      </div>
    </div>
  );
}