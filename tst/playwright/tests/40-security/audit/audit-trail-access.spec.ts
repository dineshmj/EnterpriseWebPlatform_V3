import { expect, test } from '../../../support/fixtures';
import { AuditTrail, lanIds } from '../../../support/audit';

/**
 * The audit trail is tamper-evident and watched: "Verify integrity" re-walks the hash chain, and
 * every read of the trail is itself recorded as an ACCESS entry - who looked, at what, when ("who
 * watches the watchers"). Doc: BSS-Requirements.md section 13.
 */
test.describe('Audit trail - integrity and access', () => {
  test('ShouldVerifyTheChainIntact_AndRecordTheAuditorsOwnSearch', async ({ signedInAs }) => {
    // Arrange
    const sarah = await signedInAs('sarah.audit');
    const trail = new AuditTrail(sarah);
    await trail.open();
    const mfe = sarah.workspace();
    const record = `APP-PW-ACCESS-${Date.now()}`;

    // Act + Assert 1: the hash chain is intact.
    await mfe.getByRole('button', { name: 'Verify integrity' }).click();
    await expect(mfe.getByText('The trail is intact', { exact: true })).toBeVisible({ timeout: 60_000 });

    // Act 2: a search for a record no event has.
    expect(await trail.entriesOf(record)).toEqual([]);

    // Assert 2: that search is in the trail as an ACCESS entry, by Sarah.
    await mfe.locator('#kind').selectOption('ACCESS');
    await mfe.locator('#record').fill(record);
    await mfe.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(mfe.getByText('Who read the trail', { exact: true }).last()).toBeVisible();
    const searched = mfe.locator('tbody tr').filter({ hasText: 'AuditTrailSearched' }).filter({ hasText: record });
    await expect(searched.first()).toBeVisible({ timeout: 15_000 });
    await expect(searched.first()).toContainText(lanIds['sarah.audit']);
  });
});