import { test } from '../../../support/fixtures';
import {
  expectPaymentOutcome, fundedCustomer, paymentDecision, payees, startPayment,
} from '../../../support/journeys/payments';

// ┌─ :: RESILIENCE :: A notification sent while the live connection is down still updates the page ─
// │ Why : In the Azure demo (12-NOV-2026) the live connection reconnected every 15 s; a rejection
// │       sent in such a gap reached the bell (re-read after the reconnect) but not the open payment
// │       page, which kept "Waiting for a payments officer".
// │ How : Sophie's browser goes offline (her live connection drops), Emily rejects meanwhile, Sophie
// │       comes back: after the reconnect the Shell hands the missed notification to the page, which
// │       shows the outcome by itself - no reload, no click on the bell.
// └────────────────────────────────────────────────────────────────────────────────────────────────
test.describe('Notifications across a gap in the live connection', () => {
  test.describe.configure({ timeout: 6 * 60_000 });

  test('A rejection sent while Sophie is offline still updates her open payment page', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'NotificationGap');
    const sophie = await signedInAs('sophie.cs');
    const { payments, paymentNumber } = await startPayment(sophie, customer, payees.matching, '1250.00', 'Gap');
    await expectPaymentOutcome(payments, 'Waiting for a payments officer');
    const emily = await signedInAs('emily.payments');

    // Act
    await sophie.page.context().setOffline(true);
    await paymentDecision(emily, paymentNumber, 'Reject');
    await sophie.page.context().setOffline(false);

    // Assert: the page shows the outcome once the connection is back.
    await expectPaymentOutcome(payments, 'Payment rejected', 2 * 60_000);
  });
});