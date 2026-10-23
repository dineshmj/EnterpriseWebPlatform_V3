import { expect, test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import { AT_PROXY, openAllRoutes, PlatformHealth, withRoute } from '../../../support/fault-routes';
import { expectPaymentOutcome, fundedCustomer, payees, paymentTrail } from '../../../support/journeys/payments';
import { PaymentsMfe } from '../../../support/pages/mfes';
import type { ShellPage } from '../../../support/pages/ShellPage';

/**
 * One of the platform's OWN APIs is down for its callers - cut (or hung) at the fault proxy by
 * daniel.ops on Simulator & Fault Controls, while the API itself keeps running:
 *  - the Accounts API is cut for a moment in the middle of a payment → the AccountsCommandSubscriber
 *    keeps the funds command and retries (Degraded on Platform Health, the API itself Healthy with
 *    "Cut at the proxy"); once the route is open the payment completes - one hold, settled ONCE,
 *    although the saga sent the command more than once (each repeat only repeats the answer);
 *  - it stays cut past the saga's last attempt → the reservation is given up and the funds are
 *    released - never left reserved, never lost;
 *  - the Payments API hangs (connects, never answers) → the Payments BFF gives up within its total
 *    timeout and the screen says so, instead of spinning for ever; open again, the list is back.
 * Doc: Operations Journey API README ("The fault proxy"), Features 1.7.4.
 *
 * Needs the fault proxy (ps	oxiproxyStart-Toxiproxy.ps1 starts it) and Development settings (the saga's
 * 10-second reply timer, 3 attempts; the subscribers' 10-second retry ceiling).
 */
test.describe('Platform API outage - resilience', () => {
  test.describe.configure({ timeout: 10 * 60_000 });
  test.beforeAll(async () => { await openAllRoutes(); });
  test.afterAll(async () => { await openAllRoutes(); });

  const UNAVAILABLE = 'The service is temporarily unavailable. Please try again shortly.';

  /** Sophie's New Payment form, filled in while every route is open (the payer search needs the Accounts API). */
  async function paymentReadyToTransfer(sophie: ShellPage, customerNumber: string, amount: string, reference: string): Promise<PaymentsMfe> {
    return test.step(`Sophie fills in a payment of ${amount} (not yet transferred)`, async () => {
      const payments = new PaymentsMfe(sophie);
      await payments.openNew();
      await payments.choosePayer(customerNumber);
      await payments.enterPayee(payees.matching);
      await payments.enterAmount(amount, reference);
      return payments;
    });
  }

  async function expectTrail(signedInAs: Parameters<typeof fundedCustomer>[0], paymentNumber: string,
    expected: Parameters<AuditTrail['expectJourney']>[1], forbidden: string[]) {
    return test.step('Audit Trail: what the platform recorded', async () => {
      const trail = new AuditTrail(await signedInAs('sarah.audit'));
      await trail.open();
      return trail.expectJourney(paymentNumber, expected, forbidden);
    });
  }

  test('The Accounts API is cut for a moment mid-payment: the command waits and is retried, then the payment completes - one hold, settled once', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'ApiOutage');
    const sophie = await signedInAs('sophie.cs');
    const daniel = await signedInAs('daniel.ops');
    const payments = await paymentReadyToTransfer(sophie, customer, '115.00', 'Accounts cut');

    const paymentNumber = await withRoute(daniel, 'Accounts API', 'Cut', async () => {
      // Act: the payment is accepted (the Payments API is reachable) - its funds command is not.
      const number = await payments.transfer();

      // Assert 1: Daniel sees why - the API runs, its callers cannot reach it, the subscriber retries.
      // Well inside the saga's three reply timers (10 + 20 + 40 s), so the route opens before it gives up.
      await test.step('Platform Health: Accounts API Healthy but cut at the proxy; AccountsCommandSubscriber Degraded', async () => {
        const health = new PlatformHealth(daniel);
        await health.open();
        await health.expectRow('Accounts API', AT_PROXY.Cut, 20_000);
        await health.expectRow('Accounts API', 'Healthy', 20_000);
        await health.expectRow('AccountsCommandSubscriber', 'Degraded', 30_000);
      });
      await expect(sophie.workspace().getByText('Payment completed', { exact: true }), 'nothing completes while Accounts is out of reach').toHaveCount(0);
      return number;
    });

    // Assert 2: the route is open again - the waiting command goes through and the saga finishes.
    await expectPaymentOutcome(payments, 'Payment completed', 3 * 60_000);
    const entries = await expectTrail(signedInAs, paymentNumber, paymentTrail.completed, ['FundsReleased', 'PaymentFailed']);
    // Accounts keeps ONE hold per payment: a resent ReserveFunds does not hold the money again, it
    // repeats the answer for the same hold - so the trail has one "reserved" answer per command sent.
    const reserved = entries.filter(e => e.event === 'FundsReserved');
    expect(reserved.length, 'the saga resent the command while Accounts was out of reach').toBeGreaterThanOrEqual(2);
    expect(reserved.every(e => e.status === 'HELD'), 'every answer reports the same hold, HELD').toBe(true);
    expect(entries.filter(e => e.event === 'FundsSettled'), 'the money left the account once').toHaveLength(1);
  });

  test('The Accounts API stays cut past the last attempt: the reservation is given up and the funds are released', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'ApiOutage');
    const sophie = await signedInAs('sophie.cs');
    const daniel = await signedInAs('daniel.ops');
    const payments = await paymentReadyToTransfer(sophie, customer, '116.00', 'Accounts down');

    const paymentNumber = await withRoute(daniel, 'Accounts API', 'Cut', async () => {
      // Act
      const number = await payments.transfer();

      // Assert 1: after the last reply timer the saga decides to compensate - while Accounts is still cut.
      await expect(payments.timeline().filter({ hasText: /ACCOUNTS_NOT_RESPONDING/ }), 'the saga gives up the reservation').toHaveCount(1, { timeout: 4 * 60_000 });
      await expect(sophie.workspace().getByText('Payment failed - funds released', { exact: true }),
        'nothing is claimed released before Accounts has confirmed it').toHaveCount(0);
      return number;
    });

    // Assert 2: open again - Accounts handles the reservation and then its release, in order (one key per payment).
    await expectPaymentOutcome(payments, 'Payment failed - funds released', 3 * 60_000);
    await expectTrail(signedInAs, paymentNumber, paymentTrail.failedAndReleased, ['FundsSettled', 'PaymentCompleted']);
  });

  test('The Payments API hangs: the screen says the service is unavailable within the BFF\'s timeout, and recovers once open', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    const daniel = await signedInAs('daniel.ops');

    await withRoute(daniel, 'Payments API', 'Hanging', async () => {
      // Act
      const started = Date.now();
      await sophie.openMenuItem('Payments', 'View Payments');
      await sophie.waitForScreen('Payments');

      // Assert 1: bounded - the BFF's total request timeout is 30 s (two retries of a GET, 10 s each).
      await expect(sophie.workspace().getByText(UNAVAILABLE, { exact: true })).toBeVisible({ timeout: 60_000 });
      expect(Date.now() - started, 'the person waits at most the BFF\'s total timeout, not for ever').toBeLessThan(50_000);

      // ... and Daniel sees the mark on Platform Health; the API itself still answers its probe.
      const health = new PlatformHealth(daniel);
      await health.open();
      await health.expectRow('Payments API', AT_PROXY.Hanging, 20_000);
    });

    // Assert 2: the route is open (and the BFF's circuit has closed again): the list loads.
    await expect(async () => {
      await sophie.reloadWorkspace();
      await sophie.waitForScreen('Payments');
      await expect(sophie.workspace().getByText('Loading payments…')).toHaveCount(0, { timeout: 20_000 });
      await expect(sophie.workspace().getByText(UNAVAILABLE, { exact: true })).toHaveCount(0);
    }).toPass({ timeout: 2 * 60_000 });
  });
});