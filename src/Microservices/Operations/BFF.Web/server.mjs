// Starts the Next.js app: over HTTPS on a developer's machine (the Shell is HTTPS, and cookies
// are Secure), or on plain HTTP in a container, behind the ingress that ends TLS. The development
// certificate comes from the user profile (runnow.bat), never from the repo.
import { createServer as createHttpServer } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { readFileSync } from 'node:fs';
import next from 'next';
import pg from 'pg';
import { forwardedHeaders } from './forwarded-headers.mjs';

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ A friendly console window title, like the .NET services (Common/Observability/
// │ ConsoleTitle.cs): on Windows process.title names the console window - set here, in the
// │ service itself, so pnpm and node do not leave their own command line as the title.
// └──────────────────────────────────────────────────────────────────────────────────────
process.title = 'Operations web';

const port = Number(process.env.OPERATIONS_WEB_PORT ?? '46381');

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TLS here or at the platform - never neither by accident: OPERATIONS_WEB_PLAIN_HTTP=true (the
// │ container image sets it) = plain HTTP behind the ingress; otherwise the development
// │ certificate OPERATIONS_WEB_TLS_PFX_PATH is required. Both set = refused (one is a mistake).
// └──────────────────────────────────────────────────────────────────────────────────────
const plainHttp = process.env.OPERATIONS_WEB_PLAIN_HTTP?.trim() || 'false';
if (plainHttp !== 'true' && plainHttp !== 'false') throw new Error(`OPERATIONS_WEB_PLAIN_HTTP must be true or false (is "${plainHttp}").`);
const pfx = process.env.OPERATIONS_WEB_TLS_PFX_PATH?.trim();
if (plainHttp === 'true' && pfx) throw new Error('OPERATIONS_WEB_PLAIN_HTTP=true and OPERATIONS_WEB_TLS_PFX_PATH are both set - set one of them.');
if (plainHttp === 'false' && !pfx) throw new Error('OPERATIONS_WEB_TLS_PFX_PATH is not set (see runnow.bat; behind a TLS-ending ingress set OPERATIONS_WEB_PLAIN_HTTP=true).');

// Forwarded headers only from the ingress (OPERATIONS_WEB_TRUSTED_PROXIES), one hop; see forwarded-headers.mjs.
const sanitizeForwardedHeaders = forwardedHeaders('OPERATIONS_WEB_TRUSTED_PROXIES');

// OPERATIONS_WEB_DEV=true (runnow.bat dev): development mode - no build, source maps, reload on save.
const dev = process.env.OPERATIONS_WEB_DEV === 'true';
const app = next({ dev });
const handle = app.getRequestHandler();
await app.prepare();

// ┌─ OBS + RESIL ────────────────────────────────────────────────────────────────────────
// │ Probes for the orchestrator and Operations -> Platform Health, answered before Next.js:
// │ anonymous, no session, nothing internal in the body - the same answers as the .NET
// │ components and the KYC BFF. Live = the process answers (the server listens only once the
// │ app is prepared). Ready = the session database answers too (SELECT 1 on a pool of its own,
// │ one connection, 2 s): an instance that cannot keep a session gets no traffic, instead of
// │ failing every sign-in.
// └──────────────────────────────────────────────────────────────────────────────────────
const databaseUrl = process.env.OPERATIONS_WEB_DATABASE_URL?.trim();
if (!databaseUrl) throw new Error('OPERATIONS_WEB_DATABASE_URL is not set (see runnow.bat).');
const probeDatabase = new pg.Pool({ connectionString: databaseUrl, max: 1, connectionTimeoutMillis: 2000, query_timeout: 2000 });
// A dropped idle connection is not fatal: the next probe opens a new one.
probeDatabase.on('error', () => {});
const probeHeaders = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };

async function answerReadiness(res) {
  try {
    await probeDatabase.query('SELECT 1');
    res.writeHead(200, probeHeaders);
    res.end(JSON.stringify({ status: 'Healthy', checks: [{ name: 'session-database', status: 'Healthy' }] }));
  } catch {
    res.writeHead(503, probeHeaders);
    res.end(JSON.stringify({ status: 'Unhealthy', checks: [{ name: 'session-database', status: 'Unhealthy', description: 'The session database is not reachable.' }] }));
  }
}

const onRequest = (req, res) => {
  if (req.method === 'GET' && req.url === '/health/live') {
    res.writeHead(200, probeHeaders);
    res.end('{"status":"Healthy"}');
    return;
  }
  if (req.method === 'GET' && req.url === '/health/ready') {
    void answerReadiness(res);
    return;
  }
  sanitizeForwardedHeaders(req);
  handle(req, res);
};
const server = pfx
  ? createHttpsServer({ pfx: readFileSync(pfx), passphrase: process.env.OPERATIONS_WEB_TLS_PFX_PASSWORD }, onRequest)
  : createHttpServer(onRequest);
server.listen(port, '0.0.0.0', () => console.log(`Operations web listening on ${process.env.OPERATIONS_WEB_PUBLIC_ORIGIN ?? 'https://operations.dev.localhost:' + port}${pfx ? '' : ` (plain http on port ${port}, behind the ingress)`}`));