'use client';

import { useEffect, useRef, useState } from 'react';
import { redirectToLogout, getUserDisplayName, getClaimValue, type BffUser } from '../lib/auth';
import { getDiscoveredMicroservices } from '../lib/auth-utils';
import styles from './UserProfile.module.css';

interface UserProfileProps { claims: BffUser[]; }

type IconName = 'chevron' | 'user' | 'logout';
function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  const common = { width:size, height:size, viewBox:'0 0 24 24', fill:'none', stroke:'currentColor', strokeWidth:1.8, strokeLinecap:'round' as const, strokeLinejoin:'round' as const, 'aria-hidden':true };
  if (name === 'chevron') return <svg {...common}><path d="m6 9 6 6 6-6"/></svg>;
  if (name === 'logout') return <svg {...common}><path d="M10 17l5-5-5-5"/><path d="M15 12H3"/><path d="M13 4h5a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-5"/></svg>;
  return <svg {...common}><circle cx="12" cy="8" r="3.2"/><path d="M5.5 20a6.5 6.5 0 0 1 13 0"/></svg>;
}

/** "customer_service_agent" → "Customer service agent". */
function humanizeRole(role: string) {
  const text = role.replace(/[_.-]+/g, ' ').trim();
  return text.charAt(0).toUpperCase() + text.slice(1);
}

export function UserProfile({ claims }: UserProfileProps) {
  const displayName = getUserDisplayName(claims);
  const roles = claims.filter(c => c.type === 'role').map(c => humanizeRole(String(c.value)));
  const roleCaption = roles.length === 0 ? 'Signed in' : roles.length === 1 ? roles[0] : `${roles[0]} +${roles.length - 1}`;
  const email = getClaimValue(claims, 'email') || getClaimValue(claims, 'preferred_username') || 'Signed-in user';
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const close = (event: MouseEvent) => { if (!containerRef.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, []);

  const handleLogout = async () => {
    for (const baseURL of getDiscoveredMicroservices()) {
      try {
        const response = await fetch(`${baseURL}/api/auth/silent-logout`, { method:'POST', credentials:'include' });
        if (response.status !== 200) console.warn(`Logout failed for ${baseURL} with status: ${response.status}`);
      } catch (error) { console.error(`Error during logout for ${baseURL}:`, error); }
    }
    redirectToLogout(claims);
  };

  return <div ref={containerRef} className={styles.profileArea}>
    <button type="button" className={styles.profileButton} onClick={() => setOpen(v => !v)} aria-expanded={open} aria-haspopup="menu">
      <span className={styles.avatar} aria-hidden="true"><Icon name="user" size={20} /></span>
      <span className={styles.identity}><span className={styles.name}>{displayName}</span><span className={styles.role} title={roles.join(', ') || undefined}>{roleCaption}</span></span>
      <span className={styles.profileChevron}><Icon name="chevron" size={15} /></span>
    </button>

    {open && <div className={styles.menu} role="menu">
      <div className={styles.menuHeader}><span className={styles.menuAvatar}><Icon name="user" size={21} /></span><span><strong>{displayName}</strong><small>{email}</small></span></div>
      {roles.length > 0 && <ul className={styles.roleList} aria-label="Your roles">{roles.map(r => <li key={r}>{r}</li>)}</ul>}
      <div className={styles.menuDivider} />
      <button type="button" role="menuitem" className={styles.logoutItem} onClick={handleLogout}><Icon name="logout" size={17} /><span>Sign out</span></button>
    </div>}
  </div>;
}