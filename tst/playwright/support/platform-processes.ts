import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { expect, request as playwrightRequest, test } from '@playwright/test';
import './env';
import { services } from './services';

/**
 * Local platform processes the Digital Gold deviation runs stop and start on purpose: the Kafka
 * broker (ps\kafka\Stop-Kafka.ps1 / Start-Kafka.ps1) and the Partner Products Worker
 * (ps\run\Start-NodeServices.ps1 -Only PartnerProductsWorker), plus the Temporal CLI that shows a
 * workflow's history and sends it a signal by hand.
 */

// ┌─ :: ARCHITECTURE :: Opt-in only ──────────────────────────────────────────────────────────────
// │ Why : Stopping Kafka or the worker changes the platform for EVERYBODY on this PC (every
// │       subscriber goes Degraded, every purchase stops being followed) - an ordinary run of the
// │       suite must never do that by surprise.
// │ How : Only with EWP_DEVIATION_RUNS=true in tst\playwright\.env; otherwise the spec is skipped
// │       with the reason. Each stop is paired with its start in `finally`, and the spec file's
// │       afterAll starts both again in case a run was interrupted half-way.
// │ Ref : tst/README.md "Journeys, resilience and security - what they need"
// └──────────────────────────────────────────────────────────────────────────────────────────────
export const deviationRunsEnabled = process.env.EWP_DEVIATION_RUNS === 'true';

const repositoryRoot = path.resolve(__dirname, '..', '..', '..');
const workerReadiness = services.find(s => s.name === 'Partner Products Worker')!.readiness;

/** Runs one of the repository's ps\ scripts (Windows PowerShell, no profile) and waits for it. */
function runScript(script: string, args: string[] = [], timeoutMs = 180_000): void {
  const run = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', path.join(repositoryRoot, script), ...args], {
    cwd: repositoryRoot,
    encoding: 'utf-8',
    timeout: timeoutMs,
  });
  if (run.error || run.status !== 0) {
    throw new Error(`${script} ${args.join(' ')} failed (${run.error?.message ?? `exit ${run.status}`}): ${(run.stderr || run.stdout || '').trim()}`);
  }
}

/** Answers 200 on `url` (the development certificate is trusted by the services, not always by Node). */
async function answers(url: string): Promise<boolean> {
  const api = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
  try {
    return (await api.get(url, { timeout: 5_000 })).ok();
  } catch {
    return false;
  } finally {
    await api.dispose();
  }
}

/** The .NET subscribers (ready only while connected to Kafka) - the payment saga needs them back. */
const kafkaSubscribers = services.filter(s => /^http:\/\/localhost:51\d\d\//.test(s.readiness));

// ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
// │ A process started from an administrator window cannot be stopped from a normal one
// │ (Stop-Process and taskkill are both refused), and it hides its command line from it -
// │ that is how it is recognised here. Checked before the first test, so the run says why
// │ at once instead of after the onboarding.
// └──────────────────────────────────────────────────────────────────────────────────────
/** Why this run could not stop Kafka or the worker, or undefined when it can. */
export function kafkaNotStoppableReason(): string | undefined {
  const probe = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    "foreach ($port in 9092, 46383) { " +
    "$id = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess; " +
    "if (-not $id) { \"$port=not-running\" } elseif ((Get-CimInstance Win32_Process -Filter \"ProcessId=$id\").CommandLine) { \"$port=ok\" } else { \"$port=elevated\" } }"],
  { encoding: 'utf-8', timeout: 30_000 });
  const state = Object.fromEntries((probe.stdout ?? '').trim().split(/\r?\n/).map(line => line.split('=')));
  const elevated = [state['9092'] === 'elevated' && 'Kafka', state['46383'] === 'elevated' && 'the Partner Products Worker'].filter(Boolean);
  if (state['9092'] === 'not-running') return 'Kafka is not running: start it with ps\\kafka\\Start-Kafka.ps1.';
  if (state['46383'] === 'not-running') return 'The Partner Products Worker is not running: .\\ps\\run\\Start-NodeServices.ps1 -Only PartnerProductsWorker.';
  if (elevated.length > 0) {
    return `${elevated.join(' and ')} run as administrator, so this run cannot stop them: run this test from an administrator ` +
      'PowerShell window too, or restart them from a normal one.';
  }
  return undefined;
}

export async function stopKafka(): Promise<void> {
  await test.step('Kafka stops (Stop-Kafka.ps1)', async () => { runScript('ps\\kafka\\Stop-Kafka.ps1'); });
}

/** Starts Kafka (a no-op when it runs) and waits until every Kafka subscriber is ready again. */
export async function startKafka(): Promise<void> {
  await test.step('Kafka starts again (Start-Kafka.ps1); the subscribers reconnect', async () => {
    runScript('ps\\kafka\\Start-Kafka.ps1');
    for (const subscriber of kafkaSubscribers) {
      await expect.poll(() => answers(subscriber.readiness), { message: `${subscriber.name} is ready again`, timeout: 180_000, intervals: [2_000, 5_000] }).toBe(true);
    }
  });
}

/** Kafka is down for the duration of `work`; ALWAYS started again. */
export async function withKafkaDown<T>(work: () => Promise<T>): Promise<T> {
  await stopKafka();
  try {
    return await work();
  } finally {
    await startKafka();
  }
}

export async function stopWorker(): Promise<void> {
  await test.step('The Partner Products Worker stops (its window closes)', async () => {
    runScript('ps\\run\\Start-NodeServices.ps1', ['-Only', 'PartnerProductsWorker', '-Stop']);
    await expect.poll(() => answers(workerReadiness), { message: 'the worker is down', timeout: 30_000 }).toBe(false);
  });
}

/** Starts the worker in its own window (a no-op when it runs) and waits until it polls Temporal. */
export async function startWorker(): Promise<void> {
  await test.step('The Partner Products Worker starts again in its own window', async () => {
    runScript('ps\\run\\Start-NodeServices.ps1', ['-Only', 'PartnerProductsWorker']);
    await expect.poll(() => answers(workerReadiness), { message: 'the worker is ready (connected to Temporal)', timeout: 180_000, intervals: [2_000, 5_000] }).toBe(true);
  });
}

/** For a spec file's afterAll: Kafka and the worker running, whatever the tests left behind. */
export async function ensureKafkaAndWorkerRunning(): Promise<void> {
  await startKafka();
  await startWorker();
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The Temporal development server listens on 127.0.0.1 only, with no TLS and no
// │ authorization (Threat-Model R-16) - the CLI here plays the forger the workflow must
// │ shrug off; it is never pointed at another address.
// └──────────────────────────────────────────────────────────────────────────────────────
const temporalAddress = ['--address', '127.0.0.1:7233', '--namespace', 'default'];

/** The Temporal CLI: EWP_TEMPORAL_PATH, else where winget puts it (as Start-Temporal.ps1 looks). */
export function temporalCli(): string | undefined {
  if (process.env.EWP_TEMPORAL_PATH) return process.env.EWP_TEMPORAL_PATH;
  const winget = path.join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'WinGet');
  const packages = path.join(winget, 'Packages');
  const candidates = [
    path.join(winget, 'Links', 'temporal.exe'),
    ...(fs.existsSync(packages)
      ? fs.readdirSync(packages).filter(d => d.startsWith('Temporal.TemporalCLI_')).map(d => path.join(packages, d, 'temporal.exe'))
      : []),
  ];
  return candidates.find(c => fs.existsSync(c));
}

function temporal(args: string[]): { ok: boolean; stdout: string; error: string } {
  const cli = temporalCli();
  if (!cli) throw new Error('The Temporal CLI was not found: winget install Temporal.TemporalCLI, or set EWP_TEMPORAL_PATH.');
  const run = spawnSync(cli, [...args, ...temporalAddress], { encoding: 'utf-8', timeout: 30_000 });
  return { ok: !run.error && run.status === 0, stdout: run.stdout ?? '', error: (run.error?.message ?? run.stderr ?? '').trim() };
}

/** A purchase's workflow (the Journey API starts it as gold-purchase-<number>). */
export const purchaseWorkflowId = (purchaseNumber: string) => `gold-purchase-${purchaseNumber}`;

export interface WorkflowEvent { type: string; raw: string }

/** The current run's history, each event type normalised ("EVENT_TYPE_TIMER_STARTED" -> "timerstarted"). */
export function workflowHistory(workflowId: string): WorkflowEvent[] {
  const run = temporal(['workflow', 'show', '--workflow-id', workflowId, '--output', 'json']);
  if (!run.ok) throw new Error(`temporal workflow show ${workflowId} failed: ${run.error}`);
  const parsed = JSON.parse(run.stdout) as { events?: Array<{ eventType?: string }> } | Array<{ eventType?: string }>;
  const events = Array.isArray(parsed) ? parsed : parsed.events ?? [];
  return events.map(e => ({
    type: String(e.eventType ?? '').replace(/^EVENT_TYPE_/, '').replace(/_/g, '').toLowerCase(),
    raw: JSON.stringify(e),
  }));
}

/** How often the workflow has asked the Payments API so far (its paymentOutcome activity). */
export function paymentLooks(workflowId: string): number {
  return workflowHistory(workflowId).filter(e => e.type === 'activitytaskscheduled' && e.raw.includes('"paymentOutcome"')).length;
}

/** The workflow is waiting (a timer is running: the 5-minute fallback, or a nudge ends it sooner). */
export function isWaiting(workflowId: string): boolean {
  const history = workflowHistory(workflowId);
  const started = history.filter(e => e.type === 'timerstarted').length;
  const ended = history.filter(e => e.type === 'timerfired' || e.type === 'timercanceled').length;
  return started > ended;
}

/** Sends the data-less paymentSettled signal by hand, as anyone who can reach Temporal could. */
export function signalPaymentSettled(workflowId: string): { ok: boolean; error: string } {
  const run = temporal(['workflow', 'signal', '--workflow-id', workflowId, '--name', 'paymentSettled']);
  return { ok: run.ok, error: run.error };
}