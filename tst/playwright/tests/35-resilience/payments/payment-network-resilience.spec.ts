import { expect, test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import { expectPaymentOutcome, fundedCustomer, payees, paymentTrail, startPayment } from '../../../support/journeys/payments';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * The payment network misbehaves; the saga must still end in a state that is true:
 *  - it refuses → the payment fails and the reserved funds are released;
 *  - it is down → retried with back-off (4 attempts), then failed and released;
 *  - it is down, then back → a later attempt sends the payment;
 *  - it is slow (answers after the call gave up) → the retry carries the same Idempotency-Key,
 *    so the payment is sent ONCE, never twice.
 * The simulator's switch is global, so it is always put back to Healthy (withSimulator).
 */
test.describe('Payment network resilience', () => {
  test.describe.configure({ timeout: 8 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('The network refuses: the payment fails and the reserved funds are released', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'Refusing' }, async () => {
      // Act
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '110.00', 'Refused');

      // Assert
      await expectPaymentOutcome(payments, 'Payment failed - funds released');
      await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
    });
  });

  test('The network is down: retried with back-off, then the payment fails and the funds are released', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      // Act
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '120.00', 'Down');

      // Assert: every attempt is used up before the saga gives up.
      await expectPaymentOutcome(payments, 'Payment failed - funds released', 5 * 60_000);
      await expect(payments.timeline().filter({ hasText: /Attempt 4 of 4 failed/ })).toHaveCount(1);
      await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
    });
  });

  test('The network is down, then back: a later attempt sends the payment', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    const sophie = await signedInAs('sophie.cs');

    const started = await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      const payment = await startPayment(sophie, customer, payees.matching, '130.00', 'Down then back');
      await test.step('The first attempt fails; the saga schedules the next one', async () => {
        await expect(payment.payments.timeline().filter({ hasText: /Next attempt in \d+ s/ }).first()).toBeVisible({ timeout: 90_000 });
      });
      return payment;
    });

    // The network is Healthy again (withSimulator restored it).
    await expectPaymentOutcome(started.payments, 'Payment completed', 3 * 60_000);
    await expectTrail(signedInAs, started.paymentNumber, paymentTrail.completed, ['FundsReleased', 'PaymentFailed']);
  });

  test('The network is slow: the call gives up, the retry finds the payment already sent - paid once', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'Slow' }, async () => {
      // Act
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '140.00', 'Slow');

      // Assert
      await expectPaymentOutcome(payments, 'Payment completed', 3 * 60_000);
      const entries = await expectTrail(signedInAs, paymentNumber, paymentTrail.completed, ['FundsReleased', 'PaymentFailed']);
      await test.step('Settled and completed exactly once', async () => {
        expect(entries.filter(e => e.event === 'FundsSettled')).toHaveLength(1);
        expect(entries.filter(e => e.event === 'PaymentCompleted')).toHaveLength(1);
      });
    });
  });
});

async function expectTrail(
  signedInAs: Parameters<typeof fundedCustomer>[0], paymentNumber: string,
  expected: Parameters<AuditTrail['expectJourney']>[1], forbidden: string[],
) {
  return test.step('Audit Trail: what the platform recorded', async () => {
    const trail = new AuditTrail(await signedInAs('sarah.audit'));
    await trail.open();
    return trail.expectJourney(paymentNumber, expected, forbidden);
  });
}