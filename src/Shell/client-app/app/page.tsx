'use client';

import { useState, useEffect, useRef, useCallback } from 'react';
import { AuthGuard } from './components/AuthGuard';
import { UserProfile } from './components/UserProfile';
import { NotificationBell, NotificationToasts } from './components/NotificationBell';
import { useNotifications } from './hooks/useNotifications';
import { type ShellNotification, areaOf, openablePath } from './lib/notifications';
import { ApplicationWorkspace } from './components/ApplicationWorkspace';
import { WelcomePanel } from './components/WelcomePanel';
import Image from 'next/image';
import { TopNavMenu } from './components/TopNavMenu';
import { useAuth } from './hooks/useAuth';
import { MenuResponse, MenuItem } from './types';
import { addVisitedMicroservice, setDiscoveredMicroservices } from './lib/auth-utils';
import { getClaimValue } from './lib/auth';
import { type LastVisit, readLastVisit, saveLastVisit } from './lib/last-visit';
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
  // Until the person opens a page, the Shell shows its welcome screen instead of an empty frame.
  const [frameActive, setFrameActive] = useState(false);
  const userId = user ? getClaimValue(user, 'sub') : null;
  const [lastVisit, setLastVisit] = useState<LastVisit | null>(null);
  useEffect(() => { setLastVisit(readLastVisit(userId)); }, [userId]);

  const [workspaceContext, setWorkspaceContext] =
    useState<WorkspaceContext>(EMPTY_WORKSPACE_CONTEXT);

  const currentFrameOriginRef = useRef<string | null>(null);

  // The Shell is only a transport/rendering host for workspace context.
  const currentContextRef = useRef<WorkspaceContext>(EMPTY_WORKSPACE_CONTEXT);

  // Notifications: the Shell holds the only live connection and relays each live
  // notification to the MFE in the frame (its own origin only); the MFE decides
  // whether to reload, e.g. a work queue on new work.
  const relayToFrame = useCallback((notification: ShellNotification) => {
    const iframe = document.getElementById('microservice-frame') as HTMLIFrameElement | null;
    const origin = currentFrameOriginRef.current;
    if (!iframe?.contentWindow || !origin) return;
    iframe.contentWindow.postMessage({ type: 'BSS_NOTIFICATION', notification }, origin);
  }, []);
  const notifications = useNotifications(relayToFrame);

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

  /** Menu navigation. A page opened from the menu or a workspace tile is remembered for "Resume". */
  const handleMenuItemClick = async (item: MenuItem, remember = true) => {
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
    setFrameActive(true);
    if (remember && item.microserviceName) {
      saveLastVisit(userId, { microserviceName: item.microserviceName, taskName: item.taskName, urlRelativePath: item.urlRelativePath });
    }

    // The new MFE will announce BSS_MFE_READY. At that point the Shell
    // hands it the latest opaque workspace context.
    currentFrameOriginRef.current = null;

    const silentLoginUrl =
      `${item.baseURL}/api/auth/silent-login` +
      `?returnUrl=${encodeURIComponent(item.urlRelativePath)}`;

    iframe.src = silentLoginUrl;
    iframe.onload = () => setLoading(false);
  };

  // Deep links: a notification opens only through a microservice in the person's own menu
  // that owns the path's area (e.g. "/v1/kyc"); otherwise it is shown but opens nothing.
  const ownerOf = (notification: ShellNotification) => {
    const path = openablePath(notification);
    if (!path || !menuData) return null;
    const area = areaOf(path);
    const owner = menuData.microservices.find(ms =>
      ms.managementAreas.some(ma => ma.menuItems.some(mi => areaOf(mi.urlRelativePath) === area)));
    return owner ? { path, baseURL: owner.baseURL, name: owner.name } : null;
  };

  const openNotification = (notification: ShellNotification) => {
    void notifications.markRead(notification.id);
    const owner = ownerOf(notification);
    if (!owner) return;
    void handleMenuItemClick({
      taskName: notification.title,
      urlRelativePath: owner.path,
      iconName: '',
      microserviceName: owner.name,
      baseURL: owner.baseURL,
    }, false);
  };

  // Back to the welcome screen - after the MFE in the frame agrees (unsaved changes).
  const goHome = async () => {
    const iframe = document.getElementById('microservice-frame') as HTMLIFrameElement | null;
    const currentOrigin = currentFrameOriginRef.current;
    if (iframe && currentOrigin && !(await requestNavigationPermission(iframe, currentOrigin))) return;
    currentFrameOriginRef.current = null;
    if (iframe) { iframe.onload = null; iframe.src = 'about:blank'; }
    setError(null);
    setLoading(false);
    setLastVisit(readLastVisit(userId));
    setFrameActive(false);
  };

  // "Resume" only if the remembered page is still in the person's menu (roles can change).
  const resumeItem = (() => {
    if (!lastVisit || !menuData) return null;
    for (const ms of menuData.microservices) {
      for (const area of ms.managementAreas) {
        const item = area.menuItems.find(mi => mi.urlRelativePath === lastVisit.urlRelativePath);
        if (item) return { ...item, managementAreaName: area.name, microserviceName: ms.name, baseURL: ms.baseURL };
      }
    }
    return null;
  })();

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

        <div className={styles.headerActions}>
          <NotificationBell
            items={notifications.items}
            unreadCount={notifications.unreadCount}
            status={notifications.status}
            onMarkRead={id => void notifications.markRead(id)}
            onMarkAllRead={() => void notifications.markAllRead()}
            canOpen={n => ownerOf(n) !== null}
            onOpen={openNotification}
          />
          <UserProfile claims={user} />
        </div>
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
            {frameActive && (
              <button type="button" className={styles.homeButton} onClick={() => void goHome()} title="Back to your welcome screen">
                <span aria-hidden="true">⌂</span> Home
              </button>
            )}
            <ApplicationWorkspace context={workspaceContext} />

            <span className={styles.connectionStatus}>
              <span className={notifications.status === 'live' ? styles.connectionDot : styles.connectionDotPending} />
              {notifications.status === 'live' ? 'Session active · live updates' : 'Session active · reconnecting live updates'}
            </span>
          </div>

          {error && (
            <div className={styles.error}>
              <strong>Error:</strong> {error}
            </div>
          )}

          <NotificationToasts
            toasts={notifications.toasts}
            onDismiss={notifications.dismissToast}
            canOpen={n => ownerOf(n) !== null}
            onOpen={openNotification}
          />

          {!frameActive && (
            <WelcomePanel
              claims={user}
              microservices={menuData?.microservices ?? null}
              notifications={notifications.items}
              unreadCount={notifications.unreadCount}
              canOpen={n => ownerOf(n) !== null}
              onOpenNotification={openNotification}
              resume={resumeItem}
              onNavigate={item => void handleMenuItemClick(item)}
            />
          )}

          <div className={styles.frameShell} hidden={!frameActive}>
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