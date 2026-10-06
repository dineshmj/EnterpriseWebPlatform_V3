'use client';

import { useEffect, useRef, useState } from 'react';
import type { LiveStatus } from '../hooks/useNotifications';
import { type ShellNotification, timeAgo } from '../lib/notifications';
import styles from './NotificationBell.module.css';

interface NotificationBellProps {
  items: ShellNotification[];
  unreadCount: number;
  status: LiveStatus;
  onMarkRead: (id: number) => void;
  onMarkAllRead: () => void;
  /** Whether a notification opens a record the person can reach from their menu. */
  canOpen: (notification: ShellNotification) => boolean;
  /** Marks it read and opens it (through the Shell's normal navigation). */
  onOpen: (notification: ShellNotification) => void;
}

function BellIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
      <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
    </svg>
  );
}

/**
 * The bell in the Shell's profile area: unread count, and a panel with the person's
 * latest notifications. The Shell only renders them; who receives what was decided by
 * the Notifications API from the person's token.
 */
export function NotificationBell({ items, unreadCount, status, onMarkRead, onMarkAllRead, canOpen, onOpen }: NotificationBellProps) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const close = (event: MouseEvent) => { if (!containerRef.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', escape);
    };
  }, []);

  const label = unreadCount === 0 ? 'Notifications' : `Notifications, ${unreadCount} unread`;

  return (
    <div ref={containerRef} className={styles.bellArea}>
      <button type="button" className={styles.bellButton} onClick={() => setOpen(v => !v)}
        aria-expanded={open} aria-haspopup="dialog" aria-label={label} title={label}>
        <BellIcon />
        {unreadCount > 0 && <span className={styles.badge} aria-hidden="true">{unreadCount > 99 ? '99+' : unreadCount}</span>}
        {status !== 'live' && <span className={styles.offlineDot} aria-hidden="true" />}
      </button>

      {open && (
        <div className={styles.panel} role="dialog" aria-label="Notifications">
          <div className={styles.panelHeader}>
            <strong>Notifications</strong>
            <button type="button" className={styles.linkButton} onClick={onMarkAllRead} disabled={unreadCount === 0}>
              Mark all read
            </button>
          </div>

          {status !== 'live' && (
            <p className={styles.statusNote}>
              {status === 'connecting' ? 'Connecting to live updates…' : 'Live updates paused - reconnecting. New notifications appear when the connection is back.'}
            </p>
          )}

          {items.length === 0 ? (
            <p className={styles.empty}>You&apos;re all caught up.</p>
          ) : (
            <ul className={styles.list}>
              {items.map(n => {
                const openable = canOpen(n);
                return (
                <li key={n.id}>
                  <button type="button" className={`${styles.item} ${n.read ? styles.read : ''} ${openable ? styles.openable : ''}`}
                    onClick={() => { if (openable) { setOpen(false); onOpen(n); } else { onMarkRead(n.id); } }}
                    title={openable ? 'Open' : n.read ? undefined : 'Mark as read'}>
                    <span className={n.category === 'NEW_WORK' ? styles.kindWork : styles.kindProgress} aria-hidden="true" />
                    <span className={styles.itemText}>
                      <span className={styles.itemTitle}>
                        {n.title}
                        {!n.read && <span className={styles.unreadDot} aria-label="unread" />}
                      </span>
                      <span className={styles.itemBody}>{n.body}</span>
                      <span className={styles.itemTime}>
                        {timeAgo(n.createdAt)}
                        {openable && <span className={styles.openHint}>Open ›</span>}
                      </span>
                    </span>
                  </button>
                </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

/** Live toasts, bottom right; each closes itself after a few seconds. */
interface ToastProps {
  onDismiss: (id: number) => void;
  canOpen: (notification: ShellNotification) => boolean;
  onOpen: (notification: ShellNotification) => void;
}

export function NotificationToasts({ toasts, ...props }: ToastProps & { toasts: ShellNotification[] }) {
  return (
    <div className={styles.toastStack} role="status" aria-live="polite">
      {toasts.map(t => <Toast key={t.id} toast={t} {...props} />)}
    </div>
  );
}

function Toast({ toast, onDismiss, canOpen, onOpen }: ToastProps & { toast: ShellNotification }) {
  useEffect(() => {
    const timer = window.setTimeout(() => onDismiss(toast.id), 7_000);
    return () => window.clearTimeout(timer);
  }, [toast.id, onDismiss]);

  return (
    <div className={`${styles.toast} ${toast.category === 'NEW_WORK' ? styles.toastWork : styles.toastProgress}`}>
      {canOpen(toast) ? (
        <button type="button" className={`${styles.toastText} ${styles.toastOpen}`} title="Open"
          onClick={() => { onDismiss(toast.id); onOpen(toast); }}>
          <strong>{toast.title}</strong>
          <span>{toast.body}</span>
        </button>
      ) : (
        <div className={styles.toastText}>
          <strong>{toast.title}</strong>
          <span>{toast.body}</span>
        </div>
      )}
      <button type="button" className={styles.toastClose} onClick={() => onDismiss(toast.id)} aria-label="Dismiss">×</button>
    </div>
  );
}