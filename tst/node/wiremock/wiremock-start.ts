import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/** The pinned release ps\wiremock\Install-WireMock.ps1 installs (SHA-256 checked). */
const VERSION = '3.13.2';

// ┌─ :: ARCHITECTURE :: WireMock standalone, only while the tests run ─────────────────────────────
// │ Why : The customer's NestJS teams test a service against WireMock - the real Java server, set
// │       up over its admin API - not against the real neighbours. The laptop already runs the
// │       whole platform, so the mock must cost nothing outside a test run.
// │ How : Jest's global set-up (shared by every Node.js service's tests) starts the jar on
// │       127.0.0.1 and a free port (heap capped at 128 MB), waits for its admin API and hands the
// │       address to the tests (WIREMOCK_URL); the global tear-down stops it. Each test resets it
// │       and stages only what it needs.
// │ Ref : tst/node/wiremock/wiremock.ts | ps/wiremock/Install-WireMock.ps1 | wiremock.org (admin API)
// └────────────────────────────────────────────────────────────────────────────────────────────────
export default async function startWireMock(): Promise<void> {
  const jar = process.env.WIREMOCK_JAR ?? join(process.env.LOCALAPPDATA ?? '', 'EWP', 'wiremock', `wiremock-standalone-${VERSION}.jar`);
  if (!existsSync(jar)) {
    throw new Error(`WireMock standalone ${VERSION} is not installed (${jar}). Install it once: .\\ps\\wiremock\\Install-WireMock.ps1`);
  }

  const port = await freePort();
  const url = `http://127.0.0.1:${port}`;
  const child = spawn('java', [
    '-Xmx128m', '-jar', jar,
    '--bind-address', '127.0.0.1', '--port', String(port),
    '--root-dir', mkdtempSync(join(tmpdir(), 'ewp-wiremock-')),   // no stub files of its own: every test stages its answers
    '--disable-banner',
  ], { stdio: 'ignore', windowsHide: true });
  process.env.WIREMOCK_URL = url;
  // The tear-down stops it by this ID (it runs in this same Jest process, so it sees the variable).
  process.env.WIREMOCK_PID = String(child.pid);

  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`WireMock stopped at once (exit code ${child.exitCode}). Is Java on the PATH?`);
    try {
      if ((await fetch(`${url}/__admin/mappings`)).ok) return;
    } catch {
      // not listening yet
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  child.kill();
  throw new Error(`WireMock did not answer on ${url} within 30 s.`);
}

/** A port nobody uses right now (the OS picks it). */
function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => (typeof address === 'object' && address ? resolve(address.port) : reject(new Error('No free port.'))));
    });
  });
}