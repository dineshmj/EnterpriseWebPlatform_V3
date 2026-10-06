import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

/** One notification as the Notifications API returns it (through the Shell BFF). */
export interface ShellNotification {
  id: number;
  /** PROGRESS (an application of yours moved) or NEW_WORK (work arrived for your team). */
  category: 'PROGRESS' | 'NEW_WORK' | string;
  title: string;
  body: string;
  /** What a click opens: a path in the MFE that owns the record (see openablePath). */
  target?: { mfe?: string; path?: string; recordId?: number } | null;
  createdAt: string;
  read: boolean;
}

export interface NotificationsPage {
  items: ShellNotification[];
  unreadCount: number;
}

// Both routes are proxied by the Shell BFF, which adds the person's access token.
// The REST route requires Duende's anti-forgery header; the hub is protected by an
// Origin check instead (a browser cannot add headers to a WebSocket).
const API = '/bff/notifications';
const HUB = '/hubs/notifications';
const ANTIFORGERY = { 'X-CSRF': '1' };

async function call(path: string, method: 'GET' | 'POST'): Promise<Response> {
  const response = await fetch(`${API}${path}`, { method, credentials: 'include', headers: ANTIFORGERY });
  if (!response.ok) throw new Error(`Notifications request failed (HTTP ${response.status}).`);
  return response;
}

export async function fetchNotifications(): Promise<NotificationsPage> {
  return (await call('', 'GET')).json();
}

export async function markNotificationRead(id: number): Promise<void> {
  await call(`/${id}/read`, 'POST');
}

export async function markAllNotificationsRead(): Promise<void> {
  await call('/read-all', 'POST');
}

/**
 * The live channel. Reconnects forever with back-off (1 s doubling to 30 s): the hub
 * closes a connection when its access token expires, and the next connection goes
 * through the BFF with a fresh token.
 */
export function createNotificationConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(HUB, { withCredentials: true })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: context => Math.min(30_000, 1_000 * 2 ** Math.min(context.previousRetryCount, 5)),
    })
    .configureLogging(LogLevel.Warning)
    .build();
}

// A notification may only name a page path: "/v1/<area>/<page...>", optionally with one
// numeric id parameter. No scheme, host, "//", "..", encoded characters or fragments.
const OPENABLE_PATH = /^\/v1\/[a-z0-9-]+(\/[a-z0-9-]+)*\/?(\?[A-Za-z]+=\d{1,18})?$/;

/** The first two segments, e.g. "/v1/kyc" - how a path is matched to the microservice that owns it. */
export function areaOf(path: string): string {
  return path.split(/[/?]/).slice(0, 3).join('/');
}

/**
 * The path a notification opens, or null if it opens nothing. The Shell stays business-neutral:
 * it checks only the format here, and opens the path through a microservice of the person's own
 * menu, whose BFF checks it again against its allow-list (and whose API authorizes the record).
 */
export function openablePath(notification: ShellNotification): string | null {
  const path = notification.target?.path;
  return typeof path === 'string' && OPENABLE_PATH.test(path) ? path : null;
}

/** "just now", "5 min ago", "2 h ago", otherwise the date and time. */
export function timeAgo(iso: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (seconds < 45) return 'just now';
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86_400) return `${Math.round(seconds / 3600)} h ago`;
  return new Date(iso).toLocaleString(undefined, { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
}