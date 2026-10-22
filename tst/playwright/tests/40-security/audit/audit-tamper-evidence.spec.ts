import { existsSync, mkdirSync, readdirSync, renameSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { expect, request, type APIRequestContext } from '@playwright/test';
import { test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import { literal, postgresPassword, psql, psqlOk } from '../../../support/database';

/**
 * The audit trail notices tampering by itself - nobody has to press "Verify integrity". The test
 * plays a database administrator: (1) a plain UPDATE is refused (append-only trigger); (2) with
 * the trigger switched off, one entry is edited - the Audit API's readiness turns Degraded and
 * names the entry; (3) a signed checkpoint file kept OUTSIDE the database is removed - readiness
 * names the missing checkpoint, and keeps naming it after a new checkpoint is written. Each change
 * is put back at once (finally), and the trail is intact again before the next test.
 * Doc: Features 2.4.8, Threat-Model (audit anchor), Audit API README.
 */
// ┌─ :: SECURITY :: What this test proves ─────────────────────────────────────────────────────────
// │ Why : A trail that only proves itself when someone asks is a trail nobody checks; the reviewers
// │       asked for the tamper path to be shown, not described (AI review round 4, Microsoft).
// │ How : AuditChainMonitor re-walks the chain every minute in Development (Audit:VerifyInterval-
// │       Minutes) and the readiness check "audit-chain" reports a broken chain and any checkpoint
// │       seen before that is gone. The test edits the real database and the real anchor folder.
// │ Ref : Audit API Infrastructure/AuditChainMonitor.cs, Application/AuditChainVerifier.cs |
// │       OWASP A09 | Threat-Model R-3
// └────────────────────────────────────────────────────────────────────────────────────────────────
const auditApi = 'https://audit-api.dev.localhost:46378';
const anchorFolder = resolve(__dirname, '../../../../../src/Microservices/Audit/API/AuditAnchors');
const asideFolder = resolve(__dirname, '../../../test-results/audit-anchors-aside');
// One verification a minute (Development), plus the 10-second start delay and a margin.
const noticedWithin = 150_000;

interface Check { name: string; status: string; description?: string }

async function auditChainCheck(http: APIRequestContext): Promise<Check> {
  const response = await http.get(`${auditApi}/health/ready`);
  expect(response.status(), 'Audit API readiness').toBe(200);   // Degraded still answers 200
  const check = (await response.json() as { checks: Check[] }).checks.find(c => c.name === 'audit-chain');
  expect(check, 'the audit-chain check').toBeTruthy();
  return check!;
}

async function expectChain(http: APIRequestContext, status: 'Healthy' | 'Degraded', description: RegExp, what: string): Promise<void> {
  await expect.poll(async () => {
    const check = await auditChainCheck(http);
    return `${check.status}: ${check.description ?? ''}`;
  }, { message: what, timeout: noticedWithin, intervals: [2_000, 5_000] }).toMatch(new RegExp(`^${status}: .*${description.source}`));
}

function checkpointSequences(): number[] {
  return readdirSync(anchorFolder)
    .map(f => /^checkpoint-(\d+)\.json$/.exec(f)?.[1])
    .filter((s): s is string => !!s)
    .map(Number)
    .sort((a, b) => a - b);
}

const checkpointFile = (sequence: number) => `checkpoint-${String(sequence).padStart(12, '0')}.json`;

test.describe('Audit trail - tampering is noticed without anyone asking', () => {
  test.skip(!postgresPassword, 'Set EWP_POSTGRES_PASSWORD in tst\\playwright\\.env (the PostgreSQL superuser, as for Reset.ps1).');
  test.describe.configure({ mode: 'serial', timeout: 8 * 60_000 });

  let http: APIRequestContext;
  test.beforeAll(async () => {
    http = await request.newContext({ ignoreHTTPSErrors: true });
    // Start from an intact, anchored trail.
    await expectChain(http, 'Healthy', /intact and anchored/, 'the trail is intact before the test');
  });
  test.afterAll(async () => { await http.dispose(); });

  test('ShouldRefuseAPlainEdit_AndReportAnEditedEntry_UntilItIsPutBack', async () => {
    // Arrange: the newest entry.
    const [newest] = psqlOk('EwpAuditDb', 'SELECT sequence || \'|\' || event_type FROM audit_entries ORDER BY sequence DESC LIMIT 1;');
    test.skip(!newest, 'The trail is empty: run any journey first.');
    const [sequence, eventType] = newest.split('|');

    // Act + Assert 1: the append-only trigger refuses a plain UPDATE, even for the superuser.
    const plain = psql('EwpAuditDb', `UPDATE audit_entries SET event_type = 'Edited' WHERE sequence = ${sequence};`);
    expect(plain.ok, 'a plain UPDATE').toBe(false);
    expect(plain.error).toContain('append-only');

    try {
      // Act 2: an administrator switches the trigger off for one edit (one transaction).
      psqlOk('EwpAuditDb', [
        'BEGIN;',
        'ALTER TABLE audit_entries DISABLE TRIGGER trg_audit_entries_no_change;',
        `UPDATE audit_entries SET event_type = ${literal(`${eventType}Edited`)} WHERE sequence = ${sequence};`,
        'ALTER TABLE audit_entries ENABLE TRIGGER trg_audit_entries_no_change;',
        'COMMIT;',
      ].join('\n'));

      // Assert 2: readiness turns Degraded and names the edited entry.
      await expectChain(http, 'Degraded', new RegExp(`broken at entry ${sequence}: its content no longer matches its hash`),
        `the edit of entry ${sequence} is noticed`);
    } finally {
      // Put it back exactly as it was.
      psqlOk('EwpAuditDb', [
        'BEGIN;',
        'ALTER TABLE audit_entries DISABLE TRIGGER trg_audit_entries_no_change;',
        `UPDATE audit_entries SET event_type = ${literal(eventType)} WHERE sequence = ${sequence};`,
        'ALTER TABLE audit_entries ENABLE TRIGGER trg_audit_entries_no_change;',
        'COMMIT;',
      ].join('\n'));
    }

    // Assert 3: intact again once the entry is as it was.
    await expectChain(http, 'Healthy', /intact and anchored/, 'the trail is intact again');
  });

  test('ShouldReportARemovedCheckpoint_EvenAfterTheNextCheckpointIsWritten', async ({ signedInAs }) => {
    // Arrange: an older checkpoint (not the newest: the monitor would simply write that one again).
    const before = checkpointSequences();
    test.skip(before.length < 2, 'Fewer than two checkpoints: run a journey, wait a minute, run again.');
    const removed = before[before.length - 2];
    mkdirSync(asideFolder, { recursive: true });
    const aside = join(asideFolder, checkpointFile(removed));

    try {
      // Act 1: the checkpoint file is taken away (moved aside, read-only flag and all).
      renameSync(join(anchorFolder, checkpointFile(removed)), aside);

      // Assert 1: readiness names the missing checkpoint.
      await expectChain(http, 'Degraded', new RegExp(`checkpoint file\\(s\\) missing since start-up \\(entry ${removed}\\)`),
        `the removed checkpoint ${removed} is noticed`);

      // Act 2: a new entry (Sarah searches the trail - an ACCESS entry), so a NEW checkpoint is
      // written: the number of files is back to what it was before.
      const sarah = await signedInAs('sarah.audit');
      const trail = new AuditTrail(sarah);
      await trail.open();
      await trail.entriesOf(`APP-PW-TAMPER-${Date.now()}`);
      const newest = before[before.length - 1];
      await expect.poll(() => checkpointSequences().filter(s => s > newest).length,
        { message: 'a new checkpoint is written', timeout: noticedWithin, intervals: [2_000, 5_000] }).toBeGreaterThan(0);

      // Assert 2: the removed one is still reported - a fresh anchor does not hide it.
      await expectChain(http, 'Degraded', new RegExp(`missing since start-up \\(entry ${removed}\\)`),
        'still reported after the next checkpoint');
    } finally {
      if (existsSync(aside)) renameSync(aside, join(anchorFolder, checkpointFile(removed)));
    }

    // Assert 3: healthy again once the file is back.
    await expectChain(http, 'Healthy', /intact and anchored/, 'the trail is whole again');
  });
});