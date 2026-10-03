import session from 'express-session';

/**
 * The BFF's session store, plus an index from the IDP's session ID (`sid`) and
 * subject (`sub`) to this BFF's session IDs, so a back-channel logout from the IDP
 * can end exactly the affected sessions server-side.
 *
 * In-memory, like the store itself: sessions end on restart and are not shared
 * between instances. A shared store (e.g. Redis) would hold this index as well.
 */
export const sessionStore = new session.MemoryStore();

const sessionsBySid = new Map<string, Set<string>>();
const sessionsBySub = new Map<string, Set<string>>();

export function registerSession(sessionId: string, sub: string, sid?: string): void {
  add(sessionsBySub, sub, sessionId);
  if (sid) add(sessionsBySid, sid, sessionId);
}

/** Destroys every BFF session of the IDP session (`sid`) or, without one, of the user (`sub`). */
export async function destroySessions(criteria: { sid?: string; sub?: string }): Promise<number> {
  const ids = new Set<string>();
  if (criteria.sid) sessionsBySid.get(criteria.sid)?.forEach(id => ids.add(id));
  else if (criteria.sub) sessionsBySub.get(criteria.sub)?.forEach(id => ids.add(id));

  await Promise.all(
    [...ids].map(id => new Promise<void>(resolve => sessionStore.destroy(id, () => resolve()))),
  );

  for (const index of [sessionsBySid, sessionsBySub]) {
    for (const [key, set] of index) {
      ids.forEach(id => set.delete(id));
      if (set.size === 0) index.delete(key);
    }
  }

  return ids.size;
}

function add(index: Map<string, Set<string>>, key: string, sessionId: string): void {
  const set = index.get(key) ?? new Set<string>();
  set.add(sessionId);
  index.set(key, set);
}
