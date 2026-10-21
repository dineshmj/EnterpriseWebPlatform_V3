import { spawnSync } from 'node:child_process';

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ WireMock must not outlive the test run (the laptop runs the whole platform). Stopped
// │ by its process ID: on Windows with taskkill /T /F (the JVM and anything it started),
// │ elsewhere with a signal. A WireMock already gone is fine.
// └──────────────────────────────────────────────────────────────────────────────────────
/** Jest's global tear-down: WireMock goes away with the test run (see wiremock-start.ts). */
export default async function stopWireMock(): Promise<void> {
  const pid = Number(process.env.WIREMOCK_PID);
  if (!pid) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
  } else {
    try { process.kill(pid); } catch { /* already gone */ }
  }
}