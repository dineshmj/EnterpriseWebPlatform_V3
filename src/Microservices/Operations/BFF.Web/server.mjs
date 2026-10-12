// Starts the Next.js app over HTTPS (the Shell is HTTPS, and cookies are Secure). The
// development certificate comes from the user profile (runnow.bat), never from the repo.
import { createServer } from 'node:https';
import { readFileSync } from 'node:fs';
import next from 'next';

const port = Number(process.env.OPERATIONS_WEB_PORT ?? '46381');
const pfx = process.env.OPERATIONS_WEB_TLS_PFX_PATH;
if (!pfx) throw new Error('OPERATIONS_WEB_TLS_PFX_PATH is not set (see runnow.bat).');

// OPERATIONS_WEB_DEV=true (runnow.bat dev): development mode - no build, source maps, reload on save.
const dev = process.env.OPERATIONS_WEB_DEV === 'true';
const app = next({ dev });
const handle = app.getRequestHandler();
await app.prepare();

createServer({ pfx: readFileSync(pfx), passphrase: process.env.OPERATIONS_WEB_TLS_PFX_PASSWORD }, (req, res) => handle(req, res))
  .listen(port, '0.0.0.0', () => console.log(`Operations web listening on ${process.env.OPERATIONS_WEB_PUBLIC_ORIGIN ?? 'https://operations.dev.localhost:' + port}`));