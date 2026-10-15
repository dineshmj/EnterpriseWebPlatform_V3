import { expect, test } from '../../../support/fixtures';
import { newSydneyCustomer } from '../../../support/data';
import { accountDecision, complianceDecision, expectOnboardingStatus, kycDecision, submitApplication } from '../../../support/journeys/onboarding';
import { expectNotification } from '../../../support/notifications';
import { AccountsMfe, ComplianceMfe, CustomersMfe, KycMfe, OnboardingMfe } from '../../../support/pages/mfes';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * Onboarding's edge situations, stage by stage: what the form, KYC, Compliance and Accounts refuse
 * or hold, who sees what, and who is told. (The happy path and the rejections at each stage are in
 * onboarding-journeys.spec.ts.) Doc: doc/Requirements/BSS-Requirements.md, sections 4 to 8.
 */
test.describe('Customer onboarding - edge situations', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('Submission is refused without the tax proof', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    const onboarding = new OnboardingMfe(sophie);
    await onboarding.openNew();

    // Act
    await onboarding.fillNewCustomer(newSydneyCustomer('NoTax'), undefined, true);
    const mfe = sophie.workspace();
    await mfe.getByRole('button', { name: 'Submit application', exact: true }).click();

    // Assert: the browser refuses it (the file field is required - its own bubble, not page text),
    // so the form never submits.
    const taxProof = mfe.locator('#taxProof');
    expect(await taxProof.evaluate((input: HTMLInputElement) => input.validity.valueMissing), 'tax proof missing').toBe(true);
    expect(await taxProof.evaluate((input: HTMLInputElement) => input.validationMessage), 'the browser message').not.toBe('');
    await expect(mfe.getByRole('heading', { level: 1, name: OnboardingMfe.newTitle, exact: true })).toBeVisible();
    await expect(mfe.getByRole('status').filter({ hasText: 'Application submitted' })).toHaveCount(0);
  });

  test('After a rejection the customer is a prospect again, and the list says why', async ({ signedInAs }) => {
    // Arrange: an application KYC rejects.
    const sophie = await signedInAs('sophie.cs');
    const { customerNumber, applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Again'));
    await kycDecision(await signedInAs('ethan.kyc'), applicationNumber, 'Identity verification', 'Reject');
    await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');

    // Act
    const customers = new CustomersMfe(sophie);
    await customers.open();

    // Assert: "Last application … rejected", and a new attempt may start.
    await customers.expectRowText(customerNumber, new RegExp(`Last application ${applicationNumber} rejected`));
    await expect(customers.row(customerNumber).getByRole('button', { name: /Start onboarding/ })).toBeVisible();
  });

  test('An onboarded customer cannot start another onboarding', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange: a completed onboarding.
      const sophie = await signedInAs('sophie.cs');
      const { customerNumber, applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Onboarded'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');
      await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Approve');
      await expectOnboardingStatus(sophie, applicationNumber, 'Completed');

      // Act
      const customers = new CustomersMfe(sophie);
      await customers.open();

      // Assert
      await customers.expectRowText(customerNumber, 'Onboarded');
      await expect(customers.row(customerNumber).getByRole('button', { name: /Start onboarding/ })).toHaveCount(0);
    });
  });

  test('KYC: the first officer to decide owns the case - another officer is refused', async ({ signedInAs }) => {
    // Arrange: Ethan decides the identity stage, so the case is his.
    const { applicationNumber } = await submitApplication(await signedInAs('sophie.cs'), newSydneyCustomer('Assigned'));
    await kycDecision(await signedInAs('ethan.kyc'), applicationNumber, 'Identity verification', 'Approve');

    // Act
    const noahShell = await signedInAs('noah.kyc');
    await new KycMfe(noahShell).openCase('Document verification', applicationNumber);

    // Assert: Noah sees why, and cannot decide.
    const mfe = noahShell.workspace();
    await expect(mfe.getByText(/This case is assigned to another officer/)).toBeVisible();
    await expect(mfe.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled();
  });

  test('KYC: rejecting without remarks is refused', async ({ signedInAs }) => {
    // Arrange
    const { applicationNumber } = await submitApplication(await signedInAs('sophie.cs'), newSydneyCustomer('NoRemarks'));
    const ethan = await signedInAs('ethan.kyc');
    await new KycMfe(ethan).openCase('Identity verification', applicationNumber);

    // Act
    const mfe = ethan.workspace();
    await mfe.locator('#decisionRemarks').fill('');
    await mfe.getByRole('button', { name: 'Reject', exact: true }).click();

    // Assert: refused, nothing recorded.
    await expect(mfe.getByText('Decision remarks are required when rejecting a KYC case.')).toBeVisible();
    await expect(mfe.getByRole('status').filter({ hasText: 'Decision recorded' })).toHaveCount(0);
  });

  test('Compliance: a MEDIUM-risk case (potential match) is approved by Olivia (clearance 4)', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'POTENTIAL_MATCH' }, async () => {
      // Arrange
      const { applicationNumber } = await submitApplication(await signedInAs('sophie.cs'), newSydneyCustomer('Medium'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');

      // Act
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');

      // Assert: the application reaches Accounts.
      await new AccountsMfe(await signedInAs('jack.accounts')).openCase(applicationNumber);
    });
  });

  test('Compliance: a case on hold cannot be approved until the hold is released', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const { applicationNumber } = await submitApplication(await signedInAs('sophie.cs'), newSydneyCustomer('Held'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
      const olivia = await signedInAs('olivia.compliance');
      const compliance = new ComplianceMfe(olivia);
      await compliance.openCase(applicationNumber);
      await compliance.expectCaseStatus('Under review');
      const mfe = olivia.workspace();

      // Act 1: hold.
      await mfe.locator('#officer-text').fill('Waiting for the source of funds.');
      await mfe.getByRole('button', { name: 'Put on hold', exact: true }).click();

      // Assert 1: on hold, no Approve.
      await expect(mfe.getByText('On hold', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
      await expect(mfe.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);

      // Act 2: release, then approve.
      await mfe.getByRole('button', { name: 'Release hold', exact: true }).click();
      await compliance.expectCaseStatus('Under review');
      await compliance.approve('Source of funds received; no concerns.');

      // Assert 2
      await new AccountsMfe(await signedInAs('jack.accounts')).openCase(applicationNumber);
    });
  });

  test('Accounts: an application on hold is released, then the account is opened', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const sophie = await signedInAs('sophie.cs');
      const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('AcctHeld'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');
      const jack = await signedInAs('jack.accounts');
      const accounts = new AccountsMfe(jack);
      await accounts.openCase(applicationNumber);
      await accounts.expectCaseStatus('Pending review');
      const mfe = jack.workspace();

      // Act
      await mfe.locator('#officer-text').fill('Waiting for the signed product disclosure.');
      await mfe.getByRole('button', { name: 'Put on hold', exact: true }).click();
      await expect(mfe.getByText('On hold', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
      await expect(mfe.getByRole('button', { name: 'Approve and open account', exact: true })).toHaveCount(0);
      await mfe.getByRole('button', { name: 'Release hold', exact: true }).click();
      await accounts.expectCaseStatus('Pending review');
      await accounts.approve('Disclosure signed.');

      // Assert
      await expectOnboardingStatus(sophie, applicationNumber, 'Completed');
    });
  });

  test('Notifications: the KYC officer gets the new work, the agent gets the progress', async ({ signedInAs }) => {
    // Arrange + Act
    const sophie = await signedInAs('sophie.cs');
    const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Notified'));

    // Assert 1: new work for Ethan's queue.
    const ethan = await signedInAs('ethan.kyc');
    await expectNotification(ethan, `${applicationNumber} is waiting for KYC review`);

    // Assert 2: progress for Sophie once KYC decides.
    await kycDecision(ethan, applicationNumber, 'Identity verification', 'Reject');
    await expectNotification(sophie, applicationNumber);
  });

  test('Branch scope: an agent of another branch does not see the customer', async ({ signedInAs }) => {
    // Arrange: Sophie's (Sydney) customer.
    const { customerNumber } = await submitApplication(await signedInAs('sophie.cs'), newSydneyCustomer('Scope'));

    // Act
    const customers = new CustomersMfe(await signedInAs('mia.cs'));
    await customers.open();

    // Assert: not in Mia's (Melbourne) list.
    await expect(customers.row(customerNumber)).toHaveCount(0);
  });
});