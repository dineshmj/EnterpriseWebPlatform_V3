/** What the Shell BFF says about the Shell's own session. 'unknown' = no answer (network, BFF down). */
export type SessionState = 'active' | 'ended' | 'unknown';

/** Activity renews the Shell's 30-minute session at most this often. */
const KEEP_ALIVE_MS = 5 * 60_000;

let lastKeepAlive = 0;

// ┌─ :: SECURITY :: The Shell's own session: kept alive by activity, never by machines ─────────────
// │ Why : Each application has its own BFF session; the Shell's slid only on clicks in the Shell,
// │       so it ended while the person worked inside an application, and live updates then
// │       stayed "reconnecting" for ever without saying why.
// │ How : Real activity (in the Shell, or reported by the application in the frame) renews the
// │       session at most every 5 minutes; status checks pass slide=false, so retries never keep
// │       an idle session alive. An ended session is shown as such, with "Sign in again".
// │ Ref : Features 2.3.4 | OWASP Session Management Cheat Sheet (idle timeout) | ASVS V3.3
// └────────────────────────────────────────────────────────────────────────────────────────────────
async function askBff(slide: boolean): Promise<SessionState> {
  try {
    const response = await fetch(slide ? '/bff/user' : '/bff/user?slide=false', {
      credentials: 'include',
      cache: 'no-store',
      // Duende's anti-forgery header: a cross-site page cannot add it.
      headers: { 'X-CSRF': '1' },
    });
    if (response.status === 401) return 'ended';
    if (!response.ok) return 'unknown';
    const claims: unknown = await response.json();
    return Array.isArray(claims) && claims.length > 0 ? 'active' : 'ended';
  } catch {
    return 'unknown';
  }
}

/** Is the Shell's session still active? Does NOT extend it. */
export function sessionState(): Promise<SessionState> {
  return askBff(false);
}

/**
 * The person is active: renew the Shell's session (at most every 5 minutes). Returns the state,
 * or null when it was renewed recently and nothing was asked.
 */
export async function keepSessionAlive(): Promise<SessionState | null> {
  const now = Date.now();
  if (now - lastKeepAlive < KEEP_ALIVE_MS) return null;
  lastKeepAlive = now;
  return askBff(true);
}