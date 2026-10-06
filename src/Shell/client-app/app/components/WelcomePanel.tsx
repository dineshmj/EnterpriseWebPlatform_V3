'use client';

import { getClaimValue, getUserDisplayName, type BffUser } from '../lib/auth';
import { type ShellNotification, timeAgo } from '../lib/notifications';
import type { MenuItem, Microservice } from '../types';
import styles from './WelcomePanel.module.css';

interface WelcomePanelProps {
  claims: BffUser[];
  /** The person's own menu (already filtered by role) - the only source of workspaces. */
  microservices: Microservice[] | null;
  notifications: ShellNotification[];
  unreadCount: number;
  canOpen: (notification: ShellNotification) => boolean;
  onOpenNotification: (notification: ShellNotification) => void;
  /** The last page opened, if it is still in the menu. */
  resume: MenuItem | null;
  onNavigate: (item: MenuItem) => void;
}

const MAX_UNREAD_SHOWN = 3;

function greeting(now = new Date()) {
  const hour = now.getHours();
  return hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
}

/** "customer_service_agent" → "Customer service agent". */
function humanizeRole(role: string) {
  const text = role.replace(/[_.-]+/g, ' ').trim();
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * What the Shell shows until the person opens a page: who they are (from their token), their
 * unread notifications and their workspaces (from their own menu). Business-neutral: no
 * business API is called, and every link goes through the Shell's normal navigation.
 */
export function WelcomePanel({
  claims, microservices, notifications, unreadCount, canOpen, onOpenNotification, resume, onNavigate,
}: WelcomePanelProps) {
  const firstName = getUserDisplayName(claims).split(' ')[0];
  const roles = claims.filter(c => c.type === 'role').map(c => humanizeRole(String(c.value)));
  const branch = getClaimValue(claims, 'branch');
  const branchCity = getClaimValue(claims, 'branch_city');
  const lanId = getClaimValue(claims, 'lan_id');
  const facts = [
    roles.join(', '),
    branch && (branchCity ? `${branch} · ${branchCity}` : branch),
    lanId && `LAN ID ${lanId}`,
  ].filter(Boolean) as string[];

  const unread = notifications.filter(n => !n.read).slice(0, MAX_UNREAD_SHOWN);
  const today = new Date().toLocaleDateString('en-AU', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });

  return (
    <section className={styles.welcome} aria-labelledby="welcome-title">
      <div className={styles.hero}>
        <p className={styles.date}>{today}</p>
        <h2 id="welcome-title" className={styles.title}>{greeting()}, {firstName}</h2>
        {facts.length > 0 && (
          <ul className={styles.facts} aria-label="You are signed in as">
            {facts.map(f => <li key={f}>{f}</li>)}
          </ul>
        )}
      </div>

      <div className={styles.grid}>
        <div className={styles.card}>
          <h3 className={styles.cardTitle}>
            Notifications
            {unreadCount > 0 && <span className={styles.count}>{unreadCount} unread</span>}
          </h3>
          {unread.length === 0 ? (
            <p className={styles.muted}>You&apos;re all caught up. New work and updates on your applications appear here and in the bell.</p>
          ) : (
            <ul className={styles.notes}>
              {unread.map(n => (
                <li key={n.id} className={n.category === 'NEW_WORK' ? styles.noteWork : styles.noteProgress}>
                  <span className={styles.noteText}>
                    <strong>{n.title}</strong>
                    <span>{n.body}</span>
                    <small>{timeAgo(n.createdAt)}</small>
                  </span>
                  {canOpen(n) && (
                    <button type="button" className={styles.openButton} onClick={() => onOpenNotification(n)}>Open ›</button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>

        {resume && (
          <button type="button" className={`${styles.card} ${styles.resume}`} onClick={() => onNavigate(resume)}>
            <span className={styles.resumeIcon} aria-hidden="true">↻</span>
            <span className={styles.resumeText}>
              <small>Resume where you left off</small>
              <strong>{resume.taskName}</strong>
              <span>{resume.microserviceName}</span>
            </span>
          </button>
        )}
      </div>

      <h3 className={styles.sectionTitle}>Your workspaces</h3>
      {microservices === null ? (
        <p className={styles.muted}>Loading your workspaces…</p>
      ) : microservices.length === 0 ? (
        <p className={styles.muted}>No workspaces are assigned to your role yet. Ask your administrator for access.</p>
      ) : (
        <div className={styles.workspaces}>
          {microservices.map(ms => (
            <div key={ms.name} className={styles.workspaceCard}>
              <div className={styles.workspaceHeader}>
                <span className={styles.workspaceIcon} aria-hidden="true">{ms.name.trim().charAt(0).toUpperCase()}</span>
                <strong>{ms.name}</strong>
              </div>
              <ul className={styles.tasks}>
                {ms.managementAreas.flatMap(area => area.menuItems.map(item => (
                  <li key={`${area.name}/${item.taskName}`}>
                    <button type="button" className={styles.task}
                      onClick={() => onNavigate({ ...item, managementAreaName: area.name, microserviceName: ms.name, baseURL: ms.baseURL })}>
                      {item.taskName}<span aria-hidden="true">›</span>
                    </button>
                  </li>
                )))}
              </ul>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}