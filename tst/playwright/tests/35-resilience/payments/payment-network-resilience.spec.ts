import { expect, test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import {
  decideResolution, expectPaymentOutcome, fundedCustomer, openAsOperator, payees, paymentTrail, proposeResolution, startPayment, UNCONFIRMED_ALERT,
} from '../../../support/journeys/payments';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * The payment network misbehaves; the saga must still end in a state that is true:
 *  - it refuses → the payment fails and the reserved funds are released;
 *  - it is down → retried with back-off (4 attempts); the outcome is then UNKNOWN, so the funds stay
 *    reserved while the saga asks the network - never released blind; past the alert time two
 *    operators reconcile it (four eyes), and the network's own answer always wins;
 *  - it loses its answers → the status enquiry finds the payment sent: paid once;
 *  - it is down, then back → a later attempt sends the payment;
 *  - it is slow (answers after the call gave up) → the retry carries the same Idempotency-Key,
 *    so the payment is sent ONCE, never twice.
 * The simulator's switch is global, so it is always put back to Healthy (withSimulator).
 */
test.describe('Payment network resilience', () => {
  test.describe.configure({ timeout: 15 * 60_000 });
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

  test('The network is down: after 4 attempts the outcome is unknown - the funds stay reserved, never released blind', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    const sophie = await signedInAs('sophie.cs');
    const { payments, paymentNumber } = await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      // Act
      const started = await startPayment(sophie, customer, payees.matching, '120.00', 'Down');

      // Assert 1: every attempt is used up, and still nothing is released.
      await expect(started.payments.timeline().filter({ hasText: /Attempt 4 of 4 failed/ })).toHaveCount(1, { timeout: 5 * 60_000 });
      await sophie.page.waitForTimeout(20_000);   // the saga keeps asking; it must not give up on time alone
      await expect(sophie.workspace().getByText('Payment failed - funds released', { exact: true })).toHaveCount(0);
      return started;
    });

    // Assert 2: the network is back and says it never received the payment - only now are the funds released.
    await expectPaymentOutcome(payments, 'Payment failed - funds released', 5 * 60_000);
    await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
  });

  test('The network loses its answers: the status enquiry finds the payment sent - completed, paid once', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'LosingAnswers' }, async () => {
      // Act
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '125.00', 'Lost answers');

      // Assert
      await expectPaymentOutcome(payments, 'Payment completed', 6 * 60_000);
      const entries = await expectTrail(signedInAs, paymentNumber, paymentTrail.completed, ['FundsReleased', 'PaymentFailed']);
      expect(entries.filter(e => e.event === 'FundsSettled')).toHaveLength(1);
    });
  });

  test('Unknown outcome past the alert time: Daniel proposes NOT SENT, Michael approves - the funds are released', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      // Arrange: the payment waits past the alert time.
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '135.00', 'Reconcile not sent');
      await expect(payments.timeline().filter({ hasText: UNCONFIRMED_ALERT })).toHaveCount(1, { timeout: 8 * 60_000 });

      // Act 1: Daniel proposes - and gets no Approve for his own proposal.
      const daniel = await signedInAs('daniel.ops');
      await openAsOperator(daniel, paymentNumber);
      await proposeResolution(daniel, 'NOT_SENT', 'NPP statement for today has no entry for this payment; ticket OPS-TEST-1.');
      await expect(daniel.workspace().getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);

      // Act 2: the second operator approves.
      const michael = await signedInAs('platform.admin');
      await openAsOperator(michael, paymentNumber);
      await decideResolution(michael, 'Approve');

      // Assert: released while the network is still down - on two people's evidence, not on time.
      await expectPaymentOutcome(payments, 'Payment failed - funds released', 2 * 60_000);
      await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
    });
  });

  test('Unknown outcome past the alert time: Daniel proposes SENT with the reference, Michael approves - settled', async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      // Arrange
      const { payments, paymentNumber } = await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '145.00', 'Reconcile sent');
      await expect(payments.timeline().filter({ hasText: UNCONFIRMED_ALERT })).toHaveCount(1, { timeout: 8 * 60_000 });

      // Act
      const daniel = await signedInAs('daniel.ops');
      await openAsOperator(daniel, paymentNumber);
      await proposeResolution(daniel, 'SENT', 'NPP statement line 42 shows the payment sent; ticket OPS-TEST-2.', 'NPP-TEST-000145');
      const michael = await signedInAs('platform.admin');
      await openAsOperator(michael, paymentNumber);
      await decideResolution(michael, 'Approve');

      // Assert
      await expectPaymentOutcome(payments, 'Payment completed', 2 * 60_000);
      await expectTrail(signedInAs, paymentNumber, paymentTrail.completed, ['FundsReleased', 'PaymentFailed']);
    });
  });

  test("A rejected proposal changes nothing; the network's own answer wins over an open proposal", async ({ signedInAs }) => {
    const customer = await fundedCustomer(signedInAs, 'Resilience');
    const sophie = await signedInAs('sophie.cs');
    const daniel = await signedInAs('daniel.ops');
    const { payments, paymentNumber } = await withSimulator('paymentNetwork', { behaviour: 'Down' }, async () => {
      // Arrange
      const started = await startPayment(sophie, customer, payees.matching, '155.00', 'Reconcile rejected');
      await expect(started.payments.timeline().filter({ hasText: UNCONFIRMED_ALERT })).toHaveCount(1, { timeout: 8 * 60_000 });

      // Act 1: the proposal is rejected by the second operator - the payment keeps waiting, funds still reserved.
      await openAsOperator(daniel, started.paymentNumber);
      await proposeResolution(daniel, 'NOT_SENT', 'First look: no statement entry yet; ticket OPS-TEST-3.');
      const michael = await signedInAs('platform.admin');
      await openAsOperator(michael, started.paymentNumber);
      await decideResolution(michael, 'Reject', 'The statement for today is not final yet - check again later.');
      await expect(started.payments.timeline().filter({ hasText: /rejected the proposed resolution/ })).toHaveCount(1, { timeout: 60_000 });
      await expect(sophie.workspace().getByText('Payment failed - funds released', { exact: true })).toHaveCount(0);

      // Act 2: a new proposal stays open...
      await openAsOperator(daniel, started.paymentNumber);
      await proposeResolution(daniel, 'NOT_SENT', 'Second look: still no statement entry; ticket OPS-TEST-3.');
      return started;
    });

    // Assert: ...until the network is back and answers itself - the proposal is superseded.
    await expect(payments.timeline().filter({ hasText: /is no longer needed/ })).toHaveCount(1, { timeout: 6 * 60_000 });
    await expectPaymentOutcome(payments, 'Payment failed - funds released', 2 * 60_000);
    await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
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