/**
 * "Resume where you left off": the last menu page a person opened, kept in this browser only
 * (one entry per user). A convenience - if storage is blocked or cleared, the welcome screen
 * simply shows no Resume tile. The page is reopened only if it is still in the person's menu.
 */
export interface LastVisit {
  microserviceName: string;
  taskName: string;
  urlRelativePath: string;
}

const KEY_PREFIX = 'bss.shell.last-visit.';

export function readLastVisit(userId: string | null): LastVisit | null {
  if (!userId) return null;
  try {
    const value = JSON.parse(window.localStorage.getItem(KEY_PREFIX + userId) ?? 'null');
    return value && typeof value.urlRelativePath === 'string' && typeof value.taskName === 'string'
      && typeof value.microserviceName === 'string' ? value : null;
  } catch {
    return null;
  }
}

export function saveLastVisit(userId: string | null, visit: LastVisit): void {
  if (!userId) return;
  try {
    window.localStorage.setItem(KEY_PREFIX + userId, JSON.stringify(visit));
  } catch {
    // Storage unavailable (private window, blocked site data): nothing to resume next time.
  }
}