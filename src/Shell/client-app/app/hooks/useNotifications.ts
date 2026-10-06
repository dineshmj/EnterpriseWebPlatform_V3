'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  type ShellNotification,
  createNotificationConnection,
  fetchNotifications,
  markAllNotificationsRead,
  markNotificationRead,
} from '../lib/notifications';

export type LiveStatus = 'connecting' | 'live' | 'offline';

const MAX_ITEMS = 50;
const MAX_TOASTS = 3;

/**
 * The person's notifications: loaded over REST (also after every reconnect, so nothing
 * pushed while disconnected is missed) and received live over SignalR. Every live
 * notification is also handed to <paramref name="onLive"/> - the Shell relays it to the
 * MFE in the frame, which may reload a work queue.
 */
export function useNotifications(onLive?: (notification: ShellNotification) => void) {
  const [items, setItems] = useState<ShellNotification[]>([]);
  const [unreadCount, setUnreadCount] = useState(0);
  const [toasts, setToasts] = useState<ShellNotification[]>([]);
  const [status, setStatus] = useState<LiveStatus>('connecting');
  const onLiveRef = useRef(onLive);
  onLiveRef.current = onLive;
  const itemsRef = useRef(items);
  itemsRef.current = items;

  const reload = useCallback(async () => {
    try {
      const page = await fetchNotifications();
      setItems(page.items);
      setUnreadCount(page.unreadCount);
    } catch {
      // The bell keeps what it has; the next reconnect or reload tries again.
    }
  }, []);

  useEffect(() => {
    let disposed = false;
    let retryTimer: number | undefined;
    const connection = createNotificationConnection();

    connection.on('notification', (notification: ShellNotification) => {
      setItems(previous => [notification, ...previous.filter(x => x.id !== notification.id)].slice(0, MAX_ITEMS));
      setUnreadCount(count => count + 1);
      setToasts(previous => [...previous, notification].slice(-MAX_TOASTS));
      onLiveRef.current?.(notification);
    });
    connection.onreconnecting(() => setStatus('connecting'));
    connection.onreconnected(() => { setStatus('live'); void reload(); });

    // Started, or closed without an automatic reconnect (e.g. the API was down at
    // start-up): keep trying with back-off until the page goes away.
    const start = async (attempt: number) => {
      try {
        await connection.start();
        if (disposed) { await connection.stop(); return; }
        setStatus('live');
        void reload();
      } catch {
        if (disposed) return;
        setStatus('offline');
        retryTimer = window.setTimeout(() => void start(attempt + 1), Math.min(30_000, 2_000 * 2 ** Math.min(attempt, 4)));
      }
    };
    connection.onclose(() => {
      if (disposed) return;
      setStatus('offline');
      retryTimer = window.setTimeout(() => void start(0), 2_000);
    });

    void reload();
    void start(0);

    return () => {
      disposed = true;
      window.clearTimeout(retryTimer);
      void connection.stop();
    };
  }, [reload]);

  const dismissToast = useCallback((id: number) => setToasts(previous => previous.filter(x => x.id !== id)), []);

  const markRead = useCallback(async (id: number) => {
    if (!itemsRef.current.some(x => x.id === id && !x.read)) return;
    setItems(previous => previous.map(x => (x.id === id ? { ...x, read: true } : x)));
    setUnreadCount(count => Math.max(0, count - 1));
    try { await markNotificationRead(id); } catch { void reload(); }
  }, [reload]);

  const markAllRead = useCallback(async () => {
    setItems(previous => previous.map(x => ({ ...x, read: true })));
    setUnreadCount(0);
    try { await markAllNotificationsRead(); } catch { void reload(); }
  }, [reload]);

  return { items, unreadCount, toasts, status, dismissToast, markRead, markAllRead };
}