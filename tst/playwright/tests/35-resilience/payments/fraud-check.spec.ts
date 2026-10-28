import { expect, test } from '../../../support/fixtures';
import { expectPaymentOutcome, fundedCustomer, payees, startPayment } from '../../../support/journeys/payments';
import type { Payee } from '../../../support/pages/mfes';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/** Account ending 13: on the payment network's mule list - its fraud scoring declines (demo rule). */
const muleAccount: Payee = { bsb: '083-004', accountNumber: '12345613', name: 'Quick Cash Transfers' };
/** Payees this spec's customer has never paid (Confirmation of Payee: MATCH). */
const newPayees = {
  electrician: { bsb: '083-004', accountNumber: '22345671', name: 'Riverside Electrics Pty Ltd' } satisfies Payee,
  gardener: { bsb: '083-004', accountNumber: '32345672', name: 'Greenleaf Gardens' } satisfies Payee,
};

/**
 * The fraud check at payment capture: the payment network's fraud scoring, asked within the
 * Payments API's latency budget (250 ms), and its fallback rules when the score is late or absent:
 *  - the provider declines (mule account) → the payment ends REJECTED, nothing reserved or sent;
 *  - the provider asks for a review (AUD 500+ to a payee this customer never paid) → a payments
 *    officer must approve, although the amount is below the approval tier (AUD 1,000);
 *  - the scoring is slow → the fallback decides: a payee never paid goes to an officer;
 *  - the scoring is down → the fallback lets a payment to a payee already paid follow its tier.
 * The fraud scoring's switch is its own (the payments keep flowing) and global, so it is always
 * put back to Healthy (withSimulator).
 */
test.describe('Fraud check at payment capture', () => {
  test.describe.configure({ timeout: 10 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('The provider declines a payment to a mule account: rejected at once, nothing reserved or sent', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'FraudCheck');

    // Act
    const { payments } = await startPayment(await signedInAs('sophie.cs'), customer, muleAccount, '200.00', 'Mule');

    // Assert
    await expectPaymentOutcome(payments, 'Payment rejected');
    const page = (await signedInAs('sophie.cs')).workspace();
    await expect(page.getByText(/^Decline - payment network, score 950 \(PAYEE_ON_MULE_LIST\)$/)).toBeVisible();
    await expect(payments.timeline().filter({ hasText: 'Instruction sent to Accounts' })).toHaveCount(0);
  });

  test('The provider asks for a review (AUD 600 to a new payee): a payments officer must approve, below the tier', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'FraudCheck');

    // Act
    const { payments } = await startPayment(await signedInAs('sophie.cs'), customer, newPayees.electrician, '600.00', 'New payee');

    // Assert
    await expectPaymentOutcome(payments, 'Waiting for a payments officer');
    const page = (await signedInAs('sophie.cs')).workspace();
    await expect(page.getByText('Needed (fraud check: review)', { exact: true })).toBeVisible();
    await expect(page.getByText(/^Review - payment network, score 680/)).toBeVisible();
  });

  test('The scoring is slow, then down: the fallback decides - a new payee goes to an officer, a known one is paid', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'FraudCheck');
    const sophie = await signedInAs('sophie.cs');

    // Arrange: one completed payment makes payees.matching a payee this customer has paid.
    const first = await startPayment(sophie, customer, payees.matching, '100.00', 'Known payee');
    await expectPaymentOutcome(first.payments, 'Payment completed');

    // Act + Assert 1: slow scoring (past the budget), a payee never paid → REVIEW by the fallback.
    await withSimulator('fraudScoring', { behaviour: 'Slow' }, async () => {
      const { payments } = await startPayment(sophie, customer, newPayees.gardener, '50.00', 'Slow scoring');
      await expectPaymentOutcome(payments, 'Waiting for a payments officer');
      await expect(sophie.workspace().getByText(/^Review - fallback rules: the fraud scoring did not answer in time \(PROVIDER_UNAVAILABLE, NEW_PAYEE\)$/)).toBeVisible();
    });

    // Act + Assert 2: scoring down, the payee already paid, a small amount → ALLOW by the fallback: paid.
    await withSimulator('fraudScoring', { behaviour: 'Down' }, async () => {
      const { payments } = await startPayment(sophie, customer, payees.matching, '60.00', 'Scoring down');
      await expectPaymentOutcome(payments, 'Payment completed');
      await expect(sophie.workspace().getByText(/^Allow - fallback rules: the fraud scoring did not answer in time \(PROVIDER_UNAVAILABLE\)$/)).toBeVisible();
    });
  });
});