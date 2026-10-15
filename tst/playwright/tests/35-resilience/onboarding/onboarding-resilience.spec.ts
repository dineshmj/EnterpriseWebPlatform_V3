import { expect, test } from '../../../support/fixtures';
import { newSydneyCustomer } from '../../../support/data';
import { accountDecision, complianceDecision, expectOnboardingStatus, kycDecision, submitApplication } from '../../../support/journeys/onboarding';
import { ComplianceMfe, CustomersMfe } from '../../../support/pages/mfes';
import type { ShellPage } from '../../../support/pages/ShellPage';
import type { PersonaName } from '../../../support/personas';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * Onboarding when an external system misbehaves - switched through the simulators' loopback-only
 * behaviour endpoint, no console window closed. Doc: BSS-Requirements.md sections 7 and 8.
 */
test.describe('Customer onboarding - resilience', () => {
  test.describe.configure({ timeout: 10 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  /** Submission and both KYC approvals: the application then waits for Compliance. */
  async function throughKyc(signedInAs: (username: PersonaName) => Promise<ShellPage>, label: string) {
    const sophie = await signedInAs('sophie.cs');
    const submitted = await submitApplication(sophie, newSydneyCustomer(label));
    const ethan = await signedInAs('ethan.kyc');
    await kycDecision(ethan, submitted.applicationNumber, 'Identity verification', 'Approve');
    await kycDecision(ethan, submitted.applicationNumber, 'Document verification', 'Approve');
    return { sophie, ...submitted };
  }

  test('The screening provider is down: the case waits in screening, then proceeds once it is back', async ({ signedInAs }) => {
    // Arrange + Act: KYC approves while the provider is down.
    const { applicationNumber } = await withSimulator('screeningProvider', { behaviour: 'Down' }, async () => {
      const done = await throughKyc(signedInAs, 'ScreenDown');
      const olivia = await signedInAs('olivia.compliance');
      await new ComplianceMfe(olivia).openCase(done.applicationNumber);

      // Assert 1: never decided by default - the case waits.
      await expect(olivia.workspace().getByText('The case can be decided once screening has completed.')).toBeVisible();
      return done;
    });

    // Assert 2: back up (CLEAR forced, so Olivia's clearance suffices) - a retry screens it, then it can be decided.
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      const olivia = await signedInAs('olivia.compliance');
      const compliance = new ComplianceMfe(olivia);
      await compliance.openCase(applicationNumber);
      // Retries back off from 15 s up to 5 min: allow for the next one.
      await expect(async () => {
        await olivia.reloadWorkspace();
        await expect(olivia.workspace().getByText('Under review', { exact: true }).first()).toBeVisible({ timeout: 5_000 });
      }).toPass({ timeout: 6 * 60_000, intervals: [5_000, 10_000, 15_000] });
      await compliance.approve('Screened after the provider recovered; no concerns.');
    });
  });

  test('Core banking refuses to open the account: the onboarding is compensated', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const { sophie, customerNumber, applicationNumber } = await throughKyc(signedInAs, 'CbRefused');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');

      // Act
      await withSimulator('coreBanking', { behaviour: 'Refusing' }, async () => {
        await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Approve');
        // Assert 1: rejected (compensation), at once - a refusal is not retried.
        await expectOnboardingStatus(sophie, applicationNumber, 'Rejected');
      });

      // Assert 2: the customer may try again.
      const customers = new CustomersMfe(sophie);
      await customers.open();
      await customers.expectRowText(customerNumber, new RegExp(`Last application ${applicationNumber} rejected`));
    });
  });

  test('Core banking is down for a moment: a retry opens the account, once', async ({ signedInAs }) => {
    await withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
      // Arrange
      const { sophie, applicationNumber } = await throughKyc(signedInAs, 'CbDown');
      await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');

      // Act: the first opening attempt meets a down core banking; it is back for the retry (15 s later).
      await withSimulator('coreBanking', { behaviour: 'Down' }, async () => {
        await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Approve');
      });

      // Assert
      await expectOnboardingStatus(sophie, applicationNumber, 'Completed');
    });
  });
});