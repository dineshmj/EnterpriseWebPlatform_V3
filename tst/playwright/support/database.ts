import { spawnSync } from 'node:child_process';
import './env';

/**
 * psql as the PostgreSQL superuser - for the tests that play a database administrator (the audit
 * tamper test). The same connection Reset.ps1 uses: localhost:5432, user postgres, password from
 * EWP_POSTGRES_PASSWORD in tst\playwright\.env (git-ignored).
 */
export const postgresPassword = process.env.EWP_POSTGRES_PASSWORD ?? '';
const psqlPath = process.env.EWP_PSQL_PATH || 'C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe';

export interface PsqlResult { ok: boolean; rows: string[]; error: string }

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The SQL goes in through standard input and the password through PGPASSWORD for this
// │ child process only - never on a command line another process could read.
// └──────────────────────────────────────────────────────────────────────────────────────
/** Runs `sql` in `database`; rows are psql's unaligned output (columns separated by "|"). */
export function psql(database: string, sql: string): PsqlResult {
  const run = spawnSync(psqlPath, ['-h', 'localhost', '-p', '5432', '-U', 'postgres', '-d', database, '-v', 'ON_ERROR_STOP=1', '-At', '-q', '-f', '-'], {
    input: sql,
    encoding: 'utf-8',
    env: { ...process.env, PGPASSWORD: postgresPassword, PGOPTIONS: '--client-min-messages=warning' },
    timeout: 30_000,
  });
  if (run.error) throw new Error(`psql could not run (${psqlPath}; set EWP_PSQL_PATH): ${run.error.message}`);
  return {
    ok: run.status === 0,
    rows: (run.stdout ?? '').split(/\r?\n/).filter(line => line.length > 0),
    error: (run.stderr ?? '').trim(),
  };
}

/** Like psql(), but the statement must succeed. */
export function psqlOk(database: string, sql: string): string[] {
  const result = psql(database, sql);
  if (!result.ok) throw new Error(`psql failed on ${database}: ${result.error}`);
  return result.rows;
}

/** A value as an SQL literal (NULL for null). */
export function literal(value: string | null): string {
  return value === null ? 'NULL' : `'${value.replace(/'/g, "''")}'`;
}