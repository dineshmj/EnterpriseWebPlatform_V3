import { expect, test } from '../../../support/fixtures';
import { AuditTrail, lanIds } from '../../../support/audit';
import { newSydneyCustomer } from '../../../support/data';
import { literal, postgresPassword, psqlOk } from '../../../support/database';
import { expectOnboardingStatus, kycDecision, submitApplication } from '../../../support/journeys/onboarding';
import { expectNotification } from '../../../support/notifications';
import { CustomersMfe, PersonalDataErasureMfe } from '../../../support/pages/mfes';

/**
 * Personal-data erasure (crypto-shredding), end to end: a prospect whose application KYC rejected
 * is erased at their request - Daniel asks, Michael (michael.pfadmin) approves, never Daniel himself.
 * Then the branch sees "Personal data erased", the customer can be neither found by surname nor
 * onboarded, their key is gone - in Customer Onboarding AND in each context that kept a copy (KYC's
 * case, the scans in Documents Management) - and the audit trail proves who asked, who decided,
 * and that KYC, Compliance, Accounts and Documents Management each confirmed their copy is gone.
 * The laptop has no retention period (PersonalData:Erasure:RetentionDays = 0), so a prospect made
 * a minute ago qualifies. Doc: doc/Crypto-Shredding.md.
 */
test.describe('Personal data erasure - four eyes', () => {
  test.describe.configure({ timeout: 6 * 60_000 });

  test('A rejected prospect is erased: one operator asks, another approves, and the audit trail records both', async ({ signedInAs }) => {
    // Arrange: a prospect again after KYC rejected their application.
    const sophie = await signedInAs('sophie.cs');
    const applicant = newSydneyCustomer('Erase', 'Forget');
    const { customerNumber, applicationNumber } = await submitApplication(sophie, applicant);
    await kycDecision(await signedInAs('ethan.kyc'), applicationNumber, 'Identity verification', 'Reject');
    await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
    const customers = new CustomersMfe(sophie);
    await customers.open();
    await customers.expectRowText(customerNumber, new RegExp(`Last application ${applicationNumber} rejected`));

    // Act 1: Daniel requests the erasure - and is not offered the decision.
    const danielShell = await signedInAs('daniel.ops');
    const daniel = new PersonalDataErasureMfe(danielShell);
    await test.step('Daniel requests the erasure; he cannot decide it himself', async () => {
      await daniel.open();
      await expect(await daniel.request(customerNumber)).toContainText(`Erasure of ${customerNumber} requested`);
      await daniel.expectPending(customerNumber);
      await expect(daniel.pending(customerNumber)).toContainText('Your request: another operator decides');
      await expect(daniel.pending(customerNumber).getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);
    });

    // Act 2: Michael (michael.pfadmin, also an operations administrator) is told, and approves it.
    const michaelShell = await signedInAs('michael.pfadmin');
    const michael = new PersonalDataErasureMfe(michaelShell);
    await test.step('Michael: the bell says an erasure waits for him; he approves it', async () => {
      await expectNotification(michaelShell, `asks to erase the personal data of ${customerNumber}`);
      await michael.open();
      await michael.expectPending(customerNumber);
      await michael.approve(customerNumber);
      await expect(michael.decided(customerNumber)).toContainText('ERASED', { timeout: 15_000 });
    });

    await test.step('Daniel: the bell says his request was approved', async () => {
      await expectNotification(danielShell, `the personal data of ${customerNumber} is erased`);
    });

    // Assert 1: the branch sees the customer number, no personal data, and no way to onboard them.
    await test.step('Sophie: "Personal data erased", no onboarding, not found by surname', async () => {
      await customers.expectRowText(customerNumber, 'Personal data erased');
      await expect(customers.row(customerNumber)).not.toContainText(applicant.lastName);
      await expect(customers.row(customerNumber).getByRole('button', { name: /Start onboarding/ })).toHaveCount(0);

      const mfe = sophie.workspace();
      await mfe.locator('#customer-search').fill(applicant.lastName);
      await mfe.getByRole('button', { name: 'Search', exact: true }).click();
      await expect(mfe.getByText(`No customer matches “${applicant.lastName}”`)).toBeVisible({ timeout: 15_000 });
    });

    // Assert 2: the key is gone; the ciphertext and the customer number remain.
    if (postgresPassword) {
      await test.step('Database: no key, no search index, the ciphertext still there', async () => {
        const [row] = psqlOk('EwpCustomerDb', `
          SELECT (SELECT count(*) FROM personal_data_keys k WHERE k.customer_number = c.customer_number),
                 c.email_index IS NULL, c.personal_data_erased_at IS NOT NULL, left(c.email, 3)
          FROM customers c WHERE c.customer_number = ${literal(customerNumber)};`);
        expect(row).toBe('0|t|t|v1.');
      });

      // The other contexts erase their own copies asynchronously (their subscribers): wait for them.
      await test.step("Database: KYC's key is gone too, and the scans are blanked in Documents Management", async () => {
        await expect(() => {
          const [kyc] = psqlOk('EwpKycDb', `
            SELECT (SELECT count(*) FROM personal_data_keys k WHERE k.customer_number = c.customer_number),
                   c.personal_data_erased_at IS NOT NULL, left(c.applicant, 3)
            FROM kyc_cases c WHERE c.customer_number = ${literal(customerNumber)};`);
          expect(kyc).toBe('0|t|v1.');
          const [documents] = psqlOk('EwpDocumentsManagementDb', `
            SELECT count(*), count(*) FILTER (WHERE personal_data_erased_at IS NOT NULL AND file_name = 'personal-data-erased')
            FROM documents WHERE business_reference = ${literal(customerNumber)};`);
          expect(documents).toBe('2|2');
        }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 5_000] });
      });
    }

    // Assert 3: the audit trail proves who asked, who decided, and that every copy went (LAN IDs, no personal data).
    await test.step('Sarah: the trail holds the request by Daniel, the erasure decided by Michael, and four confirmations', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      const entries = await trail.expectJourney(customerNumber, [
        { event: 'PersonalDataErasureRequested' },
        { event: 'CustomerPersonalDataErased', decidedBy: lanIds['michael.pfadmin'] },
        // KYC, Compliance, Accounts and Documents Management - each confirms, even with nothing held.
        { event: 'PersonalDataCopyErased' },
        { event: 'PersonalDataCopyErased' },
        { event: 'PersonalDataCopyErased' },
        { event: 'PersonalDataCopyErased' },
      ], ['PersonalDataErasureRejected']);
      const erased = entries.find(e => e.event === 'CustomerPersonalDataErased')!;
      expect(erased.initiatedBy, 'the requester stays the initiator').toContain(lanIds['daniel.ops']);
      expect(entries.map(e => Object.values(e).join(' ')).join(' ')).not.toContain(applicant.lastName);
    });
  });

  test('A customer with an application in progress cannot be erased', async ({ signedInAs }) => {
    // Arrange: submitted - the customer is ONBOARDING, KYC is working on them.
    const sophie = await signedInAs('sophie.cs');
    const { customerNumber } = await submitApplication(sophie, newSydneyCustomer('Busy', 'Erase'));

    // Act
    const daniel = new PersonalDataErasureMfe(await signedInAs('daniel.ops'));
    await daniel.open();
    const answer = await daniel.request(customerNumber);

    // Assert: refused at the request - nothing is waiting for a decision.
    await expect(answer).toContainText('this customer is ONBOARDING');
    await expect(daniel.pending(customerNumber)).toHaveCount(0);
  });
});