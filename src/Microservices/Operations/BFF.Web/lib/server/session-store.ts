import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'node:crypto';
import { Pool } from 'pg';
import { config, SESSION_IDLE_MINUTES } from './config';

/** What a session holds - only ever on the server; the browser has an opaque cookie. */
export interface SessionData {
  sub: string;
  sid?: string;
  lanId?: string;
  name?: string;
  idToken: string;
  accessToken: string;
  accessTokenExpiresAt: number;
  refreshToken?: string;
}

/**
 * Server-side sessions in PostgreSQL (EwpBffStateDb, schema operations_bff, user ewp_operations_web), so
 * a restart signs nobody out and several instances share them - like the .NET BFFs. A database
 * reader can neither hijack nor read a session:
 *  - the key is the SHA-256 of the cookie value, never the value itself;
 *  - the tokens are encrypted with AES-256-GCM (key OPERATIONS_WEB_SESSION_KEY, not in the database).
 */
let pool: Pool | undefined;
// ┌─ :: SECURITY :: Sessions a database reader cannot use ─────────────────────────────────────────
// │ Why : Whoever reads the session table could otherwise hijack sessions or read the tokens in
// │       them.
// │ How : Only the SHA-256 of the cookie value is stored; the tokens are encrypted with AES-256-GCM
// │       under a key that is not in the database; 30 minutes idle.
// │ Ref : Features 2.3.4 | ASVS V3.2, V6.2 | OWASP Session Management Cheat Sheet
// └────────────────────────────────────────────────────────────────────────────────────────────────
function db(): Pool {
  // RESIL: give up connecting after 5 s - an outage is answered at once (outage.ts), not after a hang.
  pool ??= new Pool({ connectionString: config.databaseUrl, max: 5, connectionTimeoutMillis: 5000 });
  return pool;
}

const hashOf = (cookieValue: string) => createHash('sha256').update(cookieValue).digest('hex');

export function encrypt(plain: string): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', config.sessionKey, iv);
  const body = Buffer.concat([cipher.update(plain, 'utf8'), cipher.final()]);
  return Buffer.concat([iv, cipher.getAuthTag(), body]).toString('base64');
}

export function decrypt(sealed: string): string {
  const raw = Buffer.from(sealed, 'base64');
  const decipher = createDecipheriv('aes-256-gcm', config.sessionKey, raw.subarray(0, 12));
  decipher.setAuthTag(raw.subarray(12, 28));
  return Buffer.concat([decipher.update(raw.subarray(28)), decipher.final()]).toString('utf8');
}

/** Creates a session and returns the cookie value (32 random bytes). */
export async function createSession(data: SessionData): Promise<string> {
  const cookieValue = randomBytes(32).toString('base64url');
  await db().query(
    `INSERT INTO operations_bff.sessions (session_hash, subject_id, sid, data, created_at, renewed_at, expires_at)
     VALUES ($1, $2, $3, $4, now(), now(), now() + make_interval(mins => $5))`,
    [hashOf(cookieValue), data.sub, data.sid ?? null, encrypt(JSON.stringify(data)), SESSION_IDLE_MINUTES],
  );
  return cookieValue;
}

/** The session for this cookie, sliding its expiry; null when unknown or expired. */
export async function readSession(cookieValue: string | undefined): Promise<SessionData | null> {
  if (!cookieValue) return null;
  const result = await db().query<{ data: string }>(
    `UPDATE operations_bff.sessions SET renewed_at = now(), expires_at = now() + make_interval(mins => $2)
     WHERE session_hash = $1 AND expires_at > now() RETURNING data`,
    [hashOf(cookieValue), SESSION_IDLE_MINUTES],
  );
  if (result.rowCount === 0) return null;
  try {
    return JSON.parse(decrypt(result.rows[0].data)) as SessionData;
  } catch {
    return null;   // tampered with, or written under another key: treat as signed out
  }
}

export async function updateSession(cookieValue: string, data: SessionData): Promise<void> {
  await db().query(`UPDATE operations_bff.sessions SET data = $2 WHERE session_hash = $1`, [hashOf(cookieValue), encrypt(JSON.stringify(data))]);
}

export async function deleteSession(cookieValue: string): Promise<void> {
  await db().query(`DELETE FROM operations_bff.sessions WHERE session_hash = $1`, [hashOf(cookieValue)]);
}

/** Front- and back-channel logout: end every session of an IDP session (sid), or of a person. */
export async function deleteSessionsOf(sid: string | undefined, sub: string | undefined): Promise<number> {
  if (sid) return (await db().query(`DELETE FROM operations_bff.sessions WHERE sid = $1`, [sid])).rowCount ?? 0;
  if (sub) return (await db().query(`DELETE FROM operations_bff.sessions WHERE subject_id = $1`, [sub])).rowCount ?? 0;
  return 0;
}

/** Expired sessions are removed now and then, by whichever request comes along. */
export async function removeExpiredSessions(): Promise<void> {
  if (Math.random() < 0.05) await db().query(`DELETE FROM operations_bff.sessions WHERE expires_at <= now()`);
}