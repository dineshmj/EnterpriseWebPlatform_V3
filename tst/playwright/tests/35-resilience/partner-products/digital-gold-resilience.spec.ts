import { request as playwrightRequest } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { lanIds } from '../../../support/audit';
import { literal, postgresPassword, psqlOk } from '../../../support/database';
import { PlatformHealth } from '../../../support/fault-routes';
import {
  allocationsOf, buyGold, formNumber, goldAccount, mobileOnRecord, outboxOf, partnerBankReachable, paymentStatusOf, purchaseNumberOf,
} from '../../../support/journeys/digital-gold';
import { paymentDecision } from '../../../support/journeys/payments';
import { DigitalGoldMfe, WORKFLOW_WAIT } from '../../../support/pages/mfes';
import {
  deviationRunsEnabled, ensureKafkaAndWorkerRunning, isWaiting, kafkaNotStoppableReason, paymentLooks, purchaseWorkflowId, signalPaymentSettled,
  startWorker, stopWorker, temporalCli, withKafkaDown,
} from '../../../support/platform-processes';

/**
 * Digital Gold when things fail - the deviation runs promised in the architect reviews (Microsoft,
 * round 3: "evidence that the controls hold when things fail"):
 *  - Kafka is down for minutes while Sophie buys gold → the purchase waits (Ordered), its events wait
 *    in the Outbox; the saga gives up, its release cannot be confirmed either and the Payments relay
 *    parks the first command → payment COMPENSATION_FAILED, Payments health flags it for operations;
 *    no money moved or held; Kafka back → the order is cancelled ONCE, no gold;
 *  - the worker stops while the purchase waits for Emily's approval, and the payment completes
 *    meanwhile → nothing moves; the worker back → Temporal replays the workflow, gold allocated ONCE;
 *  - paymentSettled is sent by hand (forged, twice) while the payment waits → each only makes the
 *    workflow look at the payment again; it carries no data, so it allocates nothing; once the
 *    workflow has ended, a late copy is refused by Temporal itself;
 *  - an Outbox message the relay gave up on (PARKED) → stays parked when Kafka is back; Daniel
 *    resends it from Platform Health with a reason → published, the resend recorded;
 *  - EWP forgot an account the Partner Bank still holds (as after an EWP database re-create) → a
 *    new Form VII is LINKED to the existing account instead of being refused (GoldAccountLinked).
 * Doc: Partner Products Worker README (controls 3, 7, 9) | Integration-Event-Catalogue.md | Features 1.3.1, 1.4.4.
 *
 * OPT-IN: stops and starts Kafka and the worker on this PC - only with EWP_DEVIATION_RUNS=true in
 * tst\playwright\.env. Also needs what 30-journeys/partner-products needs (the Partner Bank,
 * Temporal, the worker), EWP_POSTGRES_PASSWORD (the database checks) and the Temporal CLI.
 * About 30 minutes; two new customers per run.
 */
test.describe('Digital Gold deviation runs - resilience', () => {
  test.describe.configure({ mode: 'serial', timeout: 15 * 60_000 });

  test.beforeAll(async () => {
    test.skip(!deviationRunsEnabled, 'Opt-in: it stops Kafka and the Partner Products Worker - set EWP_DEVIATION_RUNS=true in tst\\playwright\\.env.');
    test.skip(!postgresPassword, 'EWP_POSTGRES_PASSWORD is not set in tst\\playwright\\.env (the database checks).');
    test.skip(!temporalCli(), 'The Temporal CLI was not found: winget install Temporal.TemporalCLI, or set EWP_TEMPORAL_PATH.');
    test.skip(!(await partnerBankReachable()), 'The Partner Bank is not reachable: start it with Start-PartnerBank.ps1 -Lan (PC) and check the hosts entries.');
    const kafkaProblem = kafkaNotStoppableReason();
    if (kafkaProblem) throw new Error(kafkaProblem);
  });
  test.afterAll(async () => {
    if (deviationRunsEnabled) await ensureKafkaAndWorkerRunning();
  });

  /** Above the approval tier (AUD 1,000): the payment waits for Emily - the workflow waits with it. */
  const ABOVE_APPROVAL_TIER = '1200.00';

  /** The customer's account at Accounts: balance and the amount held for payments in flight. */
  function accountOf(customerNumber: string): { balance: string; held: string } {
    const [row] = psqlOk('EwpAccountsDb', `SELECT balance, held_amount FROM accounts WHERE customer_number = ${literal(customerNumber)};`);
    const [balance, held] = (row ?? '|').split('|');
    return { balance, held };
  }

  /** The Payments API's readiness report (its outbox and saga backlog checks). */
  async function paymentsHealth(): Promise<{ checks: Array<{ name: string; status: string; data?: Record<string, number> }> }> {
    const api = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
    try {
      return await (await api.get('https://payments-api.dev.localhost:44488/health/ready', { timeout: 10_000 })).json();
    } finally {
      await api.dispose();
    }
  }

  // ┌─ :: RESILIENCE :: A long Kafka outage ends SAFE and FLAGGED ──────────────────────────────────
  // │ Why : The Payments saga asks Accounts (over Kafka) to reserve the funds, asks again after 10,
  // │       20 and 40 s, gives up, and asks for a release - which cannot be confirmed either; the
  // │       relay parks the first command (Development: after 3 attempts, about 2 minutes). Minutes
  // │       of outage must not move or hold money, half-pay an order or allocate gold - and must
  // │       not be silent: operations are told what to resend and retry.
  // │ How : Kafka stays down until the payment is COMPENSATION_FAILED (deterministic, no race
  // │       with the restart); then the workflow learns of it and cancels the order. The payment
  // │       is left for operations (one parked Payments message per run).
  // │ Ref : Payments saga + appsettings.Development.json Outbox | Worker README controls 3, 9 | Features 1.3.1
  // └──────────────────────────────────────────────────────────────────────────────────────────────
  test('Kafka is down for minutes while Sophie buys gold: no money moved or held, operations alerted, the order cancelled once', async ({ signedInAs }) => {
    // Arrange
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGoldDeviation');
    const sophie = await signedInAs('sophie.cs');
    const before = accountOf(customerNumber);

    const { digitalGold, slipNumber, paymentNumber } = await withKafkaDown(async () => {
      // Act: the order and the payment's start are calls (mutual TLS, https) - they go through.
      const bought = await buyGold(sophie, customerNumber, '20.00');

      // Assert 1: the payment cannot move without Kafka (the saga's commands), the events wait.
      await test.step('While Kafka is down: still Ordered, the payment not completed, the events waiting in the Outbox', async () => {
        await bought.digitalGold.expectPurchase(customerNumber, bought.slipNumber, 'Ordered - waiting for the payment');
        expect(paymentStatusOf(bought.paymentNumber)).not.toBe('COMPLETED');
        const outbox = outboxOf('GoldPurchase', purchaseNumberOf(bought.slipNumber));
        expect(outbox.length, 'the purchase\'s events are recorded').toBeGreaterThan(0);
        expect(outbox.filter(m => !m.published).length, 'and wait, unpublished - not lost').toBeGreaterThan(0);
      });

      // Assert 2: still down - the saga gives up, its release cannot be confirmed; nothing moved.
      await test.step('Still down: the saga gives up and cannot confirm the release - COMPENSATION_FAILED (Accounts not responding); no money moved or held', async () => {
        await expect.poll(() => paymentStatusOf(bought.paymentNumber), { timeout: 6 * 60_000, intervals: [5_000] }).toBe('COMPENSATION_FAILED');
        const [row] = psqlOk('EwpPaymentsDb', `SELECT outcome_code, compensation_outcome FROM payments WHERE payment_number = ${literal(bought.paymentNumber)};`);
        expect(row).toBe('ACCOUNTS_NOT_RESPONDING|FAILED');
        expect(accountOf(customerNumber), 'no funds moved or held').toEqual(before);
      });
      return bought;
    });

    // Assert 3: Kafka back - not silent: operations see what to resend and retry; still nothing moved.
    await test.step('Kafka back: the payment\'s first command stays parked; Payments health flags the parked message and the failed compensation', async () => {
      const [parked] = psqlOk('EwpPaymentsDb', `
        SELECT o.event_type FROM outbox_messages o JOIN payments p ON o.aggregate_id IN (p.id::text, p.payment_ref::text, p.payment_number)
        WHERE p.payment_number = ${literal(paymentNumber)} AND o.published_at IS NULL ORDER BY o.sequence LIMIT 1;`);
      expect(parked, 'the first unpublished message of the payment').toBe('ReserveFunds');
      const health = await paymentsHealth();
      const check = (name: string) => health.checks.find(c => c.name === name);
      expect(check('outbox-backlog')?.status).toBe('Degraded');
      expect(check('outbox-backlog')?.data?.parked ?? 0).toBeGreaterThanOrEqual(1);
      expect(check('saga-backlog')?.data?.compensationFailed ?? 0).toBeGreaterThanOrEqual(1);
      expect(accountOf(customerNumber), 'still no funds moved or held').toEqual(before);
    });

    // Assert 4: one ending for the purchase, as not paid.
    await test.step('The workflow sees the payment was not made and cancels the order - once, never allocated; its events relayed', async () => {
      await digitalGold.expectPurchase(customerNumber, slipNumber, 'Cancelled - not paid', 8 * 60_000);
      await expect(digitalGold.purchaseRow(slipNumber)).toContainText(`Payment ${paymentNumber} was not made (COMPENSATION_FAILED)`);
      const purchaseNumber = purchaseNumberOf(slipNumber);
      expect(outboxOf('GoldPurchase', purchaseNumber).filter(m => m.eventType === 'GoldPurchaseCancelled')).toHaveLength(1);
      expect(allocationsOf(purchaseNumber)).toBe(0);
      await expect.poll(() => outboxOf('GoldPurchase', purchaseNumber).filter(m => !m.published).length,
        { message: 'the Partner Products Outbox has relayed them all', timeout: 3 * 60_000, intervals: [5_000] }).toBe(0);
    });
  });

  test('The worker stops while the purchase waits for approval; the payment completes meanwhile; the worker back - allocated once', async ({ signedInAs }) => {
    // Arrange: a purchase whose workflow is waiting (its payment needs Emily's approval).
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGoldDeviation');
    const sophie = await signedInAs('sophie.cs');
    const { digitalGold, slipNumber, paymentNumber } = await buyGold(sophie, customerNumber, ABOVE_APPROVAL_TIER);
    const purchaseNumber = purchaseNumberOf(slipNumber);
    const workflowId = purchaseWorkflowId(purchaseNumber);
    await test.step('The workflow is waiting (its timer runs)', async () => {
      await expect.poll(() => isWaiting(workflowId), { timeout: 60_000 }).toBe(true);
    });

    try {
      // Act
      await stopWorker();
      await paymentDecision(await signedInAs('emily.payments'), paymentNumber, 'Approve');

      // Assert 1: the payment is done - and nobody follows it up while the worker is down.
      await test.step('The payment completes while the worker is down; the purchase stays Ordered, nothing allocated', async () => {
        await expect.poll(() => paymentStatusOf(paymentNumber), { timeout: 3 * 60_000, intervals: [3_000] }).toBe('COMPLETED');
        await sophie.page.waitForTimeout(15_000);
        await digitalGold.expectPurchase(customerNumber, slipNumber, 'Ordered - waiting for the payment');
        expect(allocationsOf(purchaseNumber)).toBe(0);
      });
    } finally {
      await startWorker();
    }

    // Assert 2: Temporal hands the waiting workflow to the new worker, which replays it and looks again.
    await test.step('The restarted worker picks the workflow up: gold allocated, once', async () => {
      await digitalGold.expectPurchase(customerNumber, slipNumber, 'Gold allocated', 8 * 60_000);
      expect(allocationsOf(purchaseNumber)).toBe(1);
    });
  });

  test('paymentSettled forged twice while the payment waits: only two extra looks, nothing allocated; a late copy after the end is refused', async ({ signedInAs }) => {
    // Arrange
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGoldDeviation');
    const sophie = await signedInAs('sophie.cs');
    const { digitalGold, slipNumber, paymentNumber } = await buyGold(sophie, customerNumber, ABOVE_APPROVAL_TIER);
    const purchaseNumber = purchaseNumberOf(slipNumber);
    const workflowId = purchaseWorkflowId(purchaseNumber);
    await expect.poll(() => isWaiting(workflowId), { message: 'the workflow is waiting', timeout: 60_000 }).toBe(true);
    const looksBefore = paymentLooks(workflowId);

    // Act + Assert 1: one signal at a time - two at once may wake the workflow only once.
    for (const copy of [1, 2]) {
      await test.step(`Forged paymentSettled #${copy}: the workflow looks at the payment again - and keeps waiting`, async () => {
        const sent = signalPaymentSettled(workflowId);
        expect(sent.ok, sent.error).toBe(true);
        await expect.poll(() => paymentLooks(workflowId), { timeout: 30_000 }).toBe(looksBefore + copy);
        await expect.poll(() => isWaiting(workflowId), { timeout: 30_000 }).toBe(true);
      });
    }
    await test.step('Still Ordered: a signal carries no outcome, the payment still waits for approval', async () => {
      await digitalGold.expectPurchase(customerNumber, slipNumber, 'Ordered - waiting for the payment');
      expect(paymentStatusOf(paymentNumber)).not.toBe('COMPLETED');
      expect(allocationsOf(purchaseNumber)).toBe(0);
    });

    // Act 2: the real outcome.
    await paymentDecision(await signedInAs('emily.payments'), paymentNumber, 'Approve');

    // Assert 2
    await test.step('Approved and paid: gold allocated, once', async () => {
      await digitalGold.expectPurchase(customerNumber, slipNumber, 'Gold allocated', 8 * 60_000);
      expect(allocationsOf(purchaseNumber)).toBe(1);
    });
    await test.step('A late copy after the end: Temporal refuses it (the workflow has completed); still one allocation', async () => {
      await expect.poll(() => signalPaymentSettled(workflowId).ok, { timeout: 30_000 }).toBe(false);
      expect(allocationsOf(purchaseNumber)).toBe(1);
    });
  });

  test('An Outbox message is parked while Kafka is down; it stays parked; Daniel resends it with a reason - published once', async ({ signedInAs }) => {
    // Arrange: a recorded slip (one event, GoldPurchaseRecorded) while Kafka is down.
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGoldDeviation');
    const digitalGold = new DigitalGoldMfe(await signedInAs('sophie.cs'));
    const daniel = await signedInAs('daniel.ops');
    const slipNumber = formNumber('TS');

    const { purchaseNumber, messageId } = await withKafkaDown(async () => {
      await test.step(`Sophie records transfer slip ${slipNumber} (not sent)`, async () => {
        await digitalGold.open();
        await digitalGold.selectCustomer(customerNumber);
        await digitalGold.recordTransferSlip(slipNumber, '20.00', mobileOnRecord);
      });
      const number = purchaseNumberOf(slipNumber);

      // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
      // │ Ten failed attempts with doubling back-off take about 43 minutes. Nine are written
      // │ here (as a DBA would see them after a long outage) and made due; the TENTH is a real
      // │ attempt against the stopped broker, and the relay parks the message itself. The row
      // │ is skipped while the relay holds it (an attempt waits up to 30 s for the broker).
      // └──────────────────────────────────────────────────────────────────────────────────────
      const id = await test.step('Fast-forward: the message has already failed 9 times (psql); the relay\'s 10th attempt fails for real', async () => {
        let updated: string[] = [];
        await expect.poll(() => {
          updated = psqlOk('EwpPartnerProductsDb', `
            UPDATE outbox_messages SET attempt_count = 9, last_attempt_at = now() - interval '1 hour'
            WHERE id IN (SELECT id FROM outbox_messages
                         WHERE aggregate_type = 'GoldPurchase' AND aggregate_id = ${literal(number)}
                           AND event_type = 'GoldPurchaseRecorded' AND published_at IS NULL
                         FOR UPDATE SKIP LOCKED)
            RETURNING id;`);
          return updated.length;
        }, { message: 'one unpublished GoldPurchaseRecorded, not held by the relay', timeout: 90_000, intervals: [2_000] }).toBe(1);
        return updated[0];
      });
      await test.step('The relay parks it (10 attempts)', async () => {
        await expect.poll(() => outboxOf('GoldPurchase', number).find(m => m.id === id)?.attemptCount,
          { timeout: 2 * 60_000, intervals: [3_000] }).toBe(10);
      });
      return { purchaseNumber: number, messageId: id };
    });

    // Assert 1: Kafka is back, but the relay leaves a parked message alone - a person decides.
    await test.step('Kafka back: the message stays parked (not retried, not published)', async () => {
      await daniel.page.waitForTimeout(20_000);
      const parked = outboxOf('GoldPurchase', purchaseNumber).find(m => m.id === messageId)!;
      expect(parked.published).toBe(false);
      expect(parked.attemptCount).toBe(10);
    });

    // Act: Daniel resends it from Platform Health.
    const health = new PlatformHealth(daniel);
    const parkedRow = () => daniel.workspace().getByRole('table', { name: 'Parked messages' }).getByRole('row').filter({ hasText: purchaseNumber });
    // The page has its own Refresh too: take the one in the parked-messages card's header (the
    // innermost element holding both the card's title and a Refresh button).
    const parkedCardRefresh = () => daniel.workspace().locator('div')
      .filter({ has: daniel.workspace().getByRole('heading', { name: /^Parked messages/ }) })
      .filter({ has: daniel.workspace().getByRole('button', { name: 'Refresh', exact: true }) })
      .last().getByRole('button', { name: 'Refresh', exact: true });
    await test.step('Daniel: Platform Health shows it parked, and he resends it with a reason', async () => {
      await health.open();
      await expect(async () => {
        await parkedCardRefresh().click();
        await expect(parkedRow()).toContainText('GoldPurchaseRecorded', { timeout: 5_000 });
      }).toPass(WORKFLOW_WAIT);
      await expect(parkedRow()).toContainText('Partner Products');
      await parkedRow().getByRole('button', { name: 'Resend', exact: true }).click();
      await daniel.workspace().getByLabel('Reason for the resend', { exact: true }).fill('Kafka was down for the deviation run; it is back and healthy.');
      await daniel.workspace().getByRole('button', { name: 'Resend', exact: true }).last().click();
      await expect(daniel.workspace().getByText('GoldPurchaseRecorded handed back to the Partner Products relay', { exact: false })).toBeVisible({ timeout: 30_000 });
    });

    // Assert 2
    await test.step('Published on the relay\'s next cycle; the resend is recorded with his LAN ID; no longer listed', async () => {
      await expect.poll(() => outboxOf('GoldPurchase', purchaseNumber).find(m => m.id === messageId)?.published,
        { timeout: 60_000, intervals: [2_000] }).toBe(true);
      const resent = outboxOf('GoldPurchase', purchaseNumber).find(m => m.id === messageId)!;
      expect(resent.redriveCount).toBe(1);
      expect(resent.lastRedrivenBy).toBe(lanIds['daniel.ops']);
      await parkedCardRefresh().click();
      await expect(parkedRow()).toHaveCount(0, { timeout: 30_000 });
    });
  });

  test('EWP forgot the account the Partner Bank still holds: a new Form VII is linked to it (GoldAccountLinked), not refused', async ({ signedInAs }) => {
    // Arrange: its own customer (its records are deleted), with an account the Partner Bank opened.
    const { customerNumber, partnerAccountNumber } = await goldAccount(signedInAs, 'DigitalGoldLinked');
    await test.step('EWP forgets the account: the customer\'s Form VII records are deleted (psql, as a database re-create would)', async () => {
      const deleted = psqlOk('EwpPartnerProductsDb', `DELETE FROM gold_account_applications WHERE customer_number = ${literal(customerNumber)} RETURNING application_number;`);
      expect(deleted, 'one opened Form VII, no purchases').toHaveLength(1);
    });

    // Act
    const sophie = await signedInAs('sophie.cs');
    const digitalGold = new DigitalGoldMfe(sophie);
    await digitalGold.open();
    await digitalGold.selectCustomer(customerNumber);
    await expect(digitalGold.viewAtPartnerBankButton(), 'EWP no longer knows an account').toHaveCount(0);
    await digitalGold.recordFormVii(formNumber('F7'), '1990-05-17');
    const linkedTo = await digitalGold.sendFormVii();

    // Assert
    await test.step('Linked to the SAME account the Partner Bank opened before - and the screen says so', async () => {
      expect(linkedTo).toBe(partnerAccountNumber);
      await expect(sophie.workspace().getByText(`Linked to account ${partnerAccountNumber}, which the Partner Bank already held for this customer.`)).toBeVisible();
    });
    await test.step('GoldAccountLinked in the Outbox (not GoldAccountOpened, not refused)', async () => {
      const [applicationNumber] = psqlOk('EwpPartnerProductsDb', `SELECT application_number FROM gold_account_applications WHERE customer_number = ${literal(customerNumber)};`);
      const events = outboxOf('GoldAccountApplication', applicationNumber).map(m => m.eventType);
      expect(events).toContain('GoldAccountLinked');
      expect(events).not.toContain('GoldAccountOpened');
      expect(events).not.toContain('GoldAccountApplicationRefused');
    });
  });
});