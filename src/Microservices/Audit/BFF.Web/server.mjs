// Starts the Next.js app: over HTTPS on a developer's machine (the Shell is HTTPS, and cookies
// are Secure), or on plain HTTP in a container, behind the ingress that ends TLS. The development
// certificate comes from the user profile (runnow.bat), never from the repo.
import { createServer as createHttpServer } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { readFileSync } from 'node:fs';
import next from 'next';
import { forwardedHeaders } from './forwarded-headers.mjs';

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ A friendly console window title, like the .NET services (Common/Observability/
// │ ConsoleTitle.cs): on Windows process.title names the console window - set here, in the
// │ service itself, so pnpm and node do not leave their own command line as the title.
// └──────────────────────────────────────────────────────────────────────────────────────
process.title = 'Audit web';

const port = Number(process.env.AUDIT_WEB_PORT ?? '46380');

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ TLS here or at the platform - never neither by accident: AUDIT_WEB_PLAIN_HTTP=true (the
// │ container image sets it) = plain HTTP behind the ingress; otherwise the development
// │ certificate AUDIT_WEB_TLS_PFX_PATH is required. Both set = refused (one is a mistake).
// └──────────────────────────────────────────────────────────────────────────────────────
const plainHttp = process.env.AUDIT_WEB_PLAIN_HTTP?.trim() || 'false';
if (plainHttp !== 'true' && plainHttp !== 'false') throw new Error(`AUDIT_WEB_PLAIN_HTTP must be true or false (is "${plainHttp}").`);
const pfx = process.env.AUDIT_WEB_TLS_PFX_PATH?.trim();
if (plainHttp === 'true' && pfx) throw new Error('AUDIT_WEB_PLAIN_HTTP=true and AUDIT_WEB_TLS_PFX_PATH are both set - set one of them.');
if (plainHttp === 'false' && !pfx) throw new Error('AUDIT_WEB_TLS_PFX_PATH is not set (see runnow.bat; behind a TLS-ending ingress set AUDIT_WEB_PLAIN_HTTP=true).');

// Forwarded headers only from the ingress (AUDIT_WEB_TRUSTED_PROXIES), one hop; see forwarded-headers.mjs.
const sanitizeForwardedHeaders = forwardedHeaders('AUDIT_WEB_TRUSTED_PROXIES');

// AUDIT_WEB_DEV=true (runnow.bat dev): development mode - no build, source maps, reload on save.
const dev = process.env.AUDIT_WEB_DEV === 'true';
const app = next({ dev });
const handle = app.getRequestHandler();
await app.prepare();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Probes for the orchestrator and Operations -> Platform Health, answered before Next.js:
// │ anonymous, no session, nothing internal in the body. Ready = the app is prepared (the
// │ server listens only after that); the session database is checked by the first sign-in.
// └──────────────────────────────────────────────────────────────────────────────────────
const probes = new Set(['/health/live', '/health/ready']);

const onRequest = (req, res) => {
  if (req.method === 'GET' && probes.has(req.url)) {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    res.end('{"status":"Healthy"}');
    return;
  }
  sanitizeForwardedHeaders(req);
  handle(req, res);
};
const server = pfx
  ? createHttpsServer({ pfx: readFileSync(pfx), passphrase: process.env.AUDIT_WEB_TLS_PFX_PASSWORD }, onRequest)
  : createHttpServer(onRequest);
server.listen(port, '0.0.0.0', () => console.log(`Audit web listening on ${process.env.AUDIT_WEB_PUBLIC_ORIGIN ?? 'https://audit.dev.localhost:' + port}${pfx ? '' : ` (plain http on port ${port}, behind the ingress)`}`));