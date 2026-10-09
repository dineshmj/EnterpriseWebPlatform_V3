import { expect, test } from '../../../support/fixtures';
import { AuditTrail, lanIds } from '../../../support/audit';
import { newSydneyCustomer } from '../../../support/data';
import {
  accountDecision, complianceDecision, expectOnboardingStatus, kycDecision, onboardingTrail, submitApplication,
} from '../../../support/journeys/onboarding';
import { ComplianceMfe } from '../../../support/pages/mfes';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * Customer onboarding end to end, one person per step, each in a browser context of their own:
 *   Sophie (agent) → Ethan (KYC) → Olivia or Grace (Compliance) → Jack (Accounts) → core banking,
 * and every way it can end. Each journey closes with Sarah reading the record's Audit Trail.
 *
 * The screening provider decides the risk from the customer number (which the platform issues),
 * so each journey FORCES the outcome it needs and the switch is restored afterwards.
 */
test.describe('Customer onboarding journeys', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('Happy path: KYC, Compliance (low risk) and Accounts approve - the account is opened', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const sophie = await signedInAs('sophie.cs');
      const customer = newSydneyCustomer('Happy');

      // Act
      const { applicationNumber } = await submitApplication(sophie, customer);
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');
      await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Approve');

      // Assert
      await expectOnboardingStatus(sophie, applicationNumber, 'Completed');
      await test.step('Audit Trail: the whole journey, in order, with its deciders', async () => {
        const trail = new AuditTrail(await signedInAs('sarah.audit'));
        await trail.open();
        await trail.expectJourney(applicationNumber, [
          ...onboardingTrail.submitted,
          ...onboardingTrail.kycApproved('ethan.kyc'),
          ...onboardingTrail.complianceScreened,
          { event: 'ComplianceCaseApproved', decidedBy: lanIds['olivia.compliance'] },
          { event: 'AccountApplicationCreated' },
          { event: 'AccountOpened' },
        ], ['KycCaseRejected', 'ComplianceCaseRejected', 'AccountApplicationRejected', 'OnboardingApplicationRejected']);
      });
    });
  });

  test('KYC identity verification rejected - the onboarding is rejected, nothing further happens', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Kycid', 'Rejected'));

    // Act
    await kycDecision(await signedInAs('ethan.kyc'), applicationNumber, 'Identity verification', 'Reject');

    // Assert
    await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
    await test.step('Audit Trail: rejected at KYC, no Compliance or Accounts', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(applicationNumber, [
        ...onboardingTrail.submitted,
        { event: 'KycIdentityVerificationRejected', decidedBy: lanIds['ethan.kyc'] },
        { event: 'KycCaseRejected' },
        { event: 'OnboardingApplicationRejected' },
      ], ['ComplianceCaseCreated', 'AccountApplicationCreated', 'AccountOpened']);
    });
  });

  test('KYC document verification rejected after identity was approved - the onboarding is rejected', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Kycdoc', 'Rejected'));
    const ethan = await signedInAs('ethan.kyc');

    // Act
    await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
    await kycDecision(ethan, applicationNumber, 'Document verification', 'Reject');

    // Assert
    await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
    await test.step('Audit Trail: identity approved, documents rejected', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(applicationNumber, [
        ...onboardingTrail.submitted,
        { event: 'KycIdentityVerificationApproved', decidedBy: lanIds['ethan.kyc'] },
        { event: 'KycDocumentVerificationRejected', decidedBy: lanIds['ethan.kyc'] },
        { event: 'KycCaseRejected' },
        { event: 'OnboardingApplicationRejected' },
      ], ['ComplianceCaseCreated', 'AccountOpened']);
    });
  });

  test('Compliance rejects - the onboarding is rejected before any account', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const sophie = await signedInAs('sophie.cs');
      const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Compliance', 'Rejected'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');

      // Act
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Reject');

      // Assert
      await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
      await test.step('Audit Trail: rejected at Compliance', async () => {
        const trail = new AuditTrail(await signedInAs('sarah.audit'));
        await trail.open();
        await trail.expectJourney(applicationNumber, [
          ...onboardingTrail.submitted,
          ...onboardingTrail.kycApproved('ethan.kyc'),
          ...onboardingTrail.complianceScreened,
          { event: 'ComplianceCaseRejected', decidedBy: lanIds['olivia.compliance'] },
          { event: 'OnboardingApplicationRejected' },
        ], ['AccountApplicationCreated', 'AccountOpened']);
      });
    });
  });

  test('High-risk screening (MATCH): Olivia (clearance 4) cannot approve, Grace (clearance 5) can', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'MATCH' }, async () => {
      // Arrange
      const sophie = await signedInAs('sophie.cs');
      const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Highrisk'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');

      // Act + Assert: Olivia lacks the clearance the case's risk requires.
      await test.step('Olivia (clearance 4) may not approve a HIGH-risk case', async () => {
        const compliance = new ComplianceMfe(await signedInAs('olivia.compliance'));
        await compliance.openCase(applicationNumber);
        await compliance.expectCaseStatus('Under review');
        await expect(compliance.approveButton()).toBeDisabled();
      });
      await complianceDecision(await signedInAs('grace.compliance'), applicationNumber, 'Approve');

      // Assert
      await test.step('Audit Trail: approved by Grace, not Olivia', async () => {
        const trail = new AuditTrail(await signedInAs('sarah.audit'));
        await trail.open();
        await trail.expectJourney(applicationNumber, [
          ...onboardingTrail.complianceScreened,
          { event: 'ComplianceCaseApproved', decidedBy: lanIds['grace.compliance'] },
          { event: 'AccountApplicationCreated' },
        ]);
      });
    });
  });

  test('The account officer rejects - the onboarding is rejected and no account is opened', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const sophie = await signedInAs('sophie.cs');
      const { applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Accounts', 'Rejected'));
      const ethan = await signedInAs('ethan.kyc');
      await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
      await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');

      // Act
      await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Reject');

      // Assert
      await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
      await test.step('Audit Trail: rejected at Accounts, no account opened', async () => {
        const trail = new AuditTrail(await signedInAs('sarah.audit'));
        await trail.open();
        await trail.expectJourney(applicationNumber, [
          { event: 'ComplianceCaseApproved', decidedBy: lanIds['olivia.compliance'] },
          { event: 'AccountApplicationCreated' },
          { event: 'AccountApplicationRejected', decidedBy: lanIds['jack.accounts'] },
          { event: 'OnboardingApplicationRejected' },
        ], ['AccountOpened']);
      });
    });
  });
});