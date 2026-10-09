import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'node:crypto';
import session, { type SessionData } from 'express-session';
import { Pool } from 'pg';

const IDLE_MS = 30 * 60 * 1000;

/**
 * Server-side sessions in PostgreSQL (EwpBffStateDb, schema kyc_bff, user ewp_kyc_bff), like
 * the .NET BFFs and the Audit web app: a restart signs nobody out, several instances share the
 * sessions, and back-channel logout ends a session whichever instance holds the browser.
 *
 * A database reader can neither hijack nor read a session:
 *  - the row key is the SHA-256 of the session ID (the ID itself is only in the signed cookie);
 *  - the whole session - tokens included - is encrypted with AES-256-GCM under KYC_BFF_SESSION_KEY,
 *    which is not in the database. A row that does not decrypt (tampered, other key) is no session.
 * The subject and the IDP session ID (sid) are kept in clear beside it, so back-channel logout
 * can find the rows to delete.
 */
export class PostgresSessionStore extends session.Store {
  constructor(private readonly pool: Pool, private readonly key: Buffer) {
    super();
    if (key.length !== 32) throw new Error('KYC_BFF_SESSION_KEY must be 32 bytes, base64-encoded.');
  }

  override get(sid: string, callback: (err: unknown, session?: SessionData | null) => void): void {
    this.pool
      .query<{ data: string }>(
        'SELECT data FROM kyc_bff.sessions WHERE session_hash = $1 AND expires_at > now()',
        [hashOf(sid)],
      )
      .then(result => {
        if (result.rowCount === 0) return callback(null, null);
        try {
          callback(null, JSON.parse(this.decrypt(result.rows[0].data)) as SessionData);
        } catch {
          callback(null, null);
        }
      })
      .catch(error => callback(error));
  }

  override set(sid: string, data: SessionData, callback?: (err?: unknown) => void): void {
    this.pool
      .query(
        `INSERT INTO kyc_bff.sessions (session_hash, subject_id, sid, data, created_at, renewed_at, expires_at)
         VALUES ($1, $2, $3, $4, now(), now(), $5)
         ON CONFLICT (session_hash) DO UPDATE
           SET subject_id = EXCLUDED.subject_id, sid = EXCLUDED.sid, data = EXCLUDED.data,
               renewed_at = now(), expires_at = EXCLUDED.expires_at`,
        [hashOf(sid), data.user?.subject ?? null, data.idpSid ?? null, this.encrypt(JSON.stringify(data)), expiryOf(data)],
      )
      .then(() => this.removeExpiredNowAndThen())
      .then(() => callback?.())
      .catch(error => callback?.(error));
  }

  /** Sliding expiry on every request (express-session calls touch when nothing else changed). */
  override touch(sid: string, data: SessionData, callback?: (err?: unknown) => void): void {
    this.pool
      .query('UPDATE kyc_bff.sessions SET renewed_at = now(), expires_at = $2 WHERE session_hash = $1', [hashOf(sid), expiryOf(data)])
      .then(() => callback?.())
      .catch(error => callback?.(error));
  }

  override destroy(sid: string, callback?: (err?: unknown) => void): void {
    this.pool
      .query('DELETE FROM kyc_bff.sessions WHERE session_hash = $1', [hashOf(sid)])
      .then(() => callback?.())
      .catch(error => callback?.(error));
  }

  /** Back-channel logout: every session of the IDP session (sid) or, without one, of the person (sub). */
  async destroySessionsOf(criteria: { sid?: string; sub?: string }): Promise<number> {
    if (criteria.sid) return (await this.pool.query('DELETE FROM kyc_bff.sessions WHERE sid = $1', [criteria.sid])).rowCount ?? 0;
    if (criteria.sub) return (await this.pool.query('DELETE FROM kyc_bff.sessions WHERE subject_id = $1', [criteria.sub])).rowCount ?? 0;
    return 0;
  }

  /** Readiness: the session database answers. */
  async ping(): Promise<void> {
    await this.pool.query('SELECT 1');
  }

  private async removeExpiredNowAndThen(): Promise<void> {
    if (Math.random() < 0.05) await this.pool.query('DELETE FROM kyc_bff.sessions WHERE expires_at <= now()');
  }

  private encrypt(plain: string): string {
    const iv = randomBytes(12);
    const cipher = createCipheriv('aes-256-gcm', this.key, iv);
    const body = Buffer.concat([cipher.update(plain, 'utf8'), cipher.final()]);
    return Buffer.concat([iv, cipher.getAuthTag(), body]).toString('base64');
  }

  private decrypt(sealed: string): string {
    const raw = Buffer.from(sealed, 'base64');
    const decipher = createDecipheriv('aes-256-gcm', this.key, raw.subarray(0, 12));
    decipher.setAuthTag(raw.subarray(12, 28));
    return Buffer.concat([decipher.update(raw.subarray(28)), decipher.final()]).toString('utf8');
  }
}

const hashOf = (sid: string) => createHash('sha256').update(sid).digest('hex');

const expiryOf = (data: SessionData) =>
  data.cookie?.expires ? new Date(data.cookie.expires) : new Date(Date.now() + IDLE_MS);

let store: PostgresSessionStore | undefined;

/** The one store of this process, shared by express-session and the back-channel logout endpoint. */
export function sessionStore(databaseUrl?: string, key?: Buffer): PostgresSessionStore {
  if (!store) {
    if (!databaseUrl || !key) throw new Error('The session store is not configured.');
    const pool = new Pool({ connectionString: databaseUrl, max: 10 });
    // An idle connection dropped by the server must not crash the process; the pool reconnects.
    pool.on('error', error => process.stderr.write(`Session database connection error: ${error.message}\n`));
    store = new PostgresSessionStore(pool, key);
  }
  return store;
}