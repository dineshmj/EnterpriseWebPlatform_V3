import { expect, test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import {
  expectPaymentOutcome, fundedCustomer, paymentDecision, payees, paymentTrail, startPayment,
} from '../../../support/journeys/payments';
import { PaymentsMfe } from '../../../support/pages/mfes';
import { resetAllSimulators } from '../../../support/simulators';

/**
 * Payments end to end - the orchestrated saga: Sophie (agent) captures a payment for a customer,
 * the Payments API reserves the funds at Accounts, Emily (payments officer) decides above the
 * approval tier (AUD 1,000), the payment network sends it, Accounts debits or releases. Each
 * journey closes with Sarah reading the payment's Audit Trail.
 *
 * The tests share one funded customer (AUD 5,000 cash deposit after onboarding), onboarded by the first test.
 */
test.describe('Payment journeys', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('Within the approval tier: no approval needed - the payment is sent and debited', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'Payments');
    const sophie = await signedInAs('sophie.cs');

    // Act
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.matching, '100.00', 'Invoice 1001');

    // Assert
    await expectPaymentOutcome(payments, 'Payment completed');
    await test.step('Audit Trail: reserved, settled, completed - nothing released', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(paymentNumber, paymentTrail.completed, ['PaymentApprovalRequired', 'FundsReleased', 'PaymentRejected', 'PaymentFailed']);
    });
  });

  test('Above the approval tier: Emily approves - the payment is sent', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'Payments');
    const sophie = await signedInAs('sophie.cs');
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.matching, '1100.00', 'Invoice 1002');
    await expectPaymentOutcome(payments, 'Waiting for a payments officer');

    // Act
    await paymentDecision(await signedInAs('emily.payments'), paymentNumber, 'Approve');

    // Assert
    await expectPaymentOutcome(payments, 'Payment completed');
    await test.step('Audit Trail: approval required, approved by Emily, settled', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(paymentNumber, paymentTrail.approved('emily.payments'), ['FundsReleased', 'PaymentRejected', 'PaymentFailed']);
    });
  });

  test('Above the approval tier: Emily rejects - nothing is sent and the funds are released', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'Payments');
    const sophie = await signedInAs('sophie.cs');
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.matching, '1200.00', 'Invoice 1003');
    await expectPaymentOutcome(payments, 'Waiting for a payments officer');

    // Act
    await paymentDecision(await signedInAs('emily.payments'), paymentNumber, 'Reject');

    // Assert
    await expectPaymentOutcome(payments, 'Payment rejected');
    await test.step('Audit Trail: rejected by Emily, the reservation released, nothing settled', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(paymentNumber, paymentTrail.rejectedByOfficer('emily.payments'), ['FundsSettled', 'PaymentCompleted']);
    });
  });

  test('The payee\'s bank is closed (BSB 999): the network refuses - the payment fails and the funds are released', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'Payments');
    const sophie = await signedInAs('sophie.cs');

    // Act
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.closedBank, '150.00', 'Old supplier');

    // Assert
    await expectPaymentOutcome(payments, 'Payment failed - funds released');
    await test.step('Audit Trail: reserved, released, failed - nothing settled', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      await trail.expectJourney(paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
    });
  });

  test('The screen stops a payment that is not ready: Confirmation of Payee and available funds', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'Payments');
    const payments = new PaymentsMfe(await signedInAs('sophie.cs'));
    await payments.openNew();
    await payments.choosePayer(customer);
    await payments.enterAmount('100.00');

    await test.step('Close match: the bank\'s own name can be taken over, which is a match', async () => {
      await payments.enterPayee(payees.closeMatch);
      await expect(payments.confirmationOfPayee()).toHaveText('Confirmation of Payee: close match');
      await expect(payments.transferButton()).toBeDisabled();
      await payments.useHeldName();
      await expect(payments.confirmationOfPayee()).toHaveText('Confirmation of Payee: match');
      await expect(payments.transferButton()).toBeEnabled();
    });

    await test.step('No match: Transfer stays disabled until the agent confirms the details with the customer', async () => {
      await payments.enterPayee(payees.noMatch);
      await expect(payments.confirmationOfPayee()).toHaveText('Confirmation of Payee: no match');
      await expect(payments.transferButton()).toBeDisabled();
      await expect(payments.stillToDo()).toContainText('Confirm the payee (Confirmation of Payee).');
    });

    await test.step('More than the available funds: refused on the screen before anything is reserved', async () => {
      await payments.enterPayee(payees.matching);
      await payments.enterAmount('999999.00');
      await expect(payments.stillToDo()).toContainText('The amount is more than the available funds.');
      await expect(payments.transferButton()).toBeDisabled();
    });
  });
});