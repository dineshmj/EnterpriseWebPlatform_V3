import { expect, test } from '../../../support/fixtures';
import { expectPaymentOutcome, fundedCustomer, payees, paymentDecision, startPayment } from '../../../support/journeys/payments';
import { expectNotification } from '../../../support/notifications';
import { PaymentApprovalsMfe } from '../../../support/pages/mfes';
import { resetAllSimulators } from '../../../support/simulators';

/**
 * Payment edge situations at the approval stage, and who is told. (The happy paths, the closed bank
 * and the screen's own checks are in payment-journeys.spec.ts; the network's failures and the
 * four-eyes reconciliation in 35-resilience.) Doc: BSS-Requirements.md section 10.
 */
test.describe('Payment journeys - edge situations', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('Rejecting a payment needs remarks', async ({ signedInAs }) => {
    // Arrange: a payment above the approval tier.
    const customer = await fundedCustomer(signedInAs, 'PaymentEdges');
    const { paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '1200.00', 'Needs remarks');
    const emily = await signedInAs('emily.payments');
    await new PaymentApprovalsMfe(emily).openPayment(paymentNumber);
    const mfe = emily.workspace();
    const reject = mfe.getByRole('button', { name: 'Reject', exact: true }).first();

    // Act + Assert: no remarks, no rejection; remarks enable it.
    await expect(reject).toBeDisabled();
    await mfe.locator('#decision-remarks').fill('The customer cancelled by phone.');
    await expect(reject).toBeEnabled();
  });

  test('Notifications: the payments officer gets the approval request, the agent gets the outcome', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'PaymentEdges');
    const sophie = await signedInAs('sophie.cs');

    // Act
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.matching, '1300.00', 'Notified');

    // Assert 1: new work for Emily's queue.
    const emily = await signedInAs('emily.payments');
    await expectNotification(emily, `${customer} needs a payments officer's approval`);

    // Assert 2: Emily approves; Sophie is told.
    await paymentDecision(emily, paymentNumber, 'Approve');
    await expectPaymentOutcome(payments, 'Payment completed', 2 * 60_000);
    await expectNotification(sophie, paymentNumber);
  });
});