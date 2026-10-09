import { expect } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';

/**
 * The closing step of every journey: Sarah (the auditor) searches the record in the Audit Trail
 * and the test checks what the platform RECORDED - not only what the screens showed:
 *  - the expected events, IN THIS ORDER (other entries in between are fine: a retry, a late reply);
 *  - who decided each step (LAN IDs - the trail stores no names);
 *  - events that must NOT be there (e.g. no account opened after a rejection).
 * The trail fills from Kafka a moment after the screens move on, so it is searched again until
 * everything expected has arrived.
 */
export interface ExpectedEntry {
  event: string;
  /** The LAN ID in "Decided by", when the step is a person's decision. */
  decidedBy?: string;
}

export interface TrailEntry {
  sequence: number;
  event: string;
  record: string;
  initiatedBy: string;
  decidedBy: string;
  status: string;
}

/** LAN IDs of the demo people (IDP seed), as the trail shows them. */
export const lanIds = {
  'sophie.cs': 'somit', 'mia.cs': 'mirob',
  'ethan.kyc': 'etpar', 'liam.kyc': 'liand', 'noah.kyc': 'nohug',
  'olivia.compliance': 'olben', 'grace.compliance': 'grwal',
  'jack.accounts': 'jawil', 'emily.payments': 'emcar', 'daniel.ops': 'dacoo', 'sarah.audit': 'sacol',
} as const;

export class AuditTrail {
  constructor(private readonly sarah: ShellPage) {}

  async open(): Promise<void> {
    await this.sarah.openMenuItem('Audit', 'Audit Trail');
    await this.sarah.waitForScreen('Audit Trail');
  }

  /** Searches one record (APP-…, PAY-…, CUST-…) and returns its entries, oldest first. */
  async entriesOf(record: string): Promise<TrailEntry[]> {
    const mfe = this.sarah.workspace();
    await mfe.locator('#record').fill(record);
    await mfe.getByRole('button', { name: 'Search', exact: true }).click();
    const rows = mfe.locator('tbody tr');
    // Wait for THIS search's results: the table still shows the previous search until they load.
    await expect(async () => {
      const nothing = await mfe.getByText('Nothing found', { exact: true }).isVisible();
      const records = (await rows.locator('td:nth-child(4)').allInnerTexts()).map(r => r.trim());
      expect(nothing || (records.length > 0 && records.every(r => r.includes(record))), `results for ${record}`).toBe(true);
    }).toPass({ timeout: 15_000 });
    const entries: TrailEntry[] = [];
    for (const row of await rows.all()) {
      const cells = (await row.locator('td').allInnerTexts()).map(c => c.trim());
      if (cells.length < 7) continue;
      entries.push({
        sequence: Number(cells[0].replace(/\D/g, '')),
        event: cells[2].split('\n')[0].trim(),
        record: cells[3],
        initiatedBy: cells[4],
        decidedBy: cells[5],
        status: cells[6],
      });
    }
    return entries.sort((a, b) => a.sequence - b.sequence);
  }

  /** The record's trail holds `expected` in order (with the right deciders) and none of `forbidden`. */
  async expectJourney(record: string, expected: ExpectedEntry[], forbidden: string[] = []): Promise<TrailEntry[]> {
    let entries: TrailEntry[] = [];
    await expect(async () => {
      entries = await this.entriesOf(record);
      expect(missingInOrder(entries, expected), `not (yet) in the trail of ${record}, in order`).toEqual([]);
    }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000, 5_000] });

    const present = forbidden.filter(f => entries.some(e => e.event === f));
    expect(present, `must not be in the trail of ${record}`).toEqual([]);
    return entries;
  }
}

/** The expected entries not found as an ordered subsequence (with matching deciders). */
function missingInOrder(entries: TrailEntry[], expected: ExpectedEntry[]): string[] {
  let from = 0;
  const missing: string[] = [];
  for (const want of expected) {
    const at = entries.findIndex((e, i) => i >= from && e.event === want.event && (!want.decidedBy || e.decidedBy.includes(want.decidedBy)));
    if (at < 0) missing.push(want.decidedBy ? `${want.event} (decided by ${want.decidedBy})` : want.event);
    else from = at + 1;
  }
  return missing;
}