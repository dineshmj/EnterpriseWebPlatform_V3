import { condition, continueAsNew, log, proxyActivities, setHandler } from '@temporalio/workflow';
import type { Activities } from './activities';
import { paymentSettledSignal } from './signals';

export interface GoldPurchaseInput {
  purchaseNumber: string;
  /** How long this purchase has waited for a payment to be started (carried over by continue-as-new). */
  waitedWithoutPaymentSeconds?: number;
}

export type GoldPurchaseOutcome = 'ALLOCATED' | 'CANCELLED' | 'REFUSED';

const { purchaseState, paymentOutcome, sendPaymentAdvice, cancelOrder } = proxyActivities<Activities>({
  startToCloseTimeout: '30 seconds',
  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ The Partner Bank, the IDP or an API may be down for a while: retry with back-off, for
  // │ as long as it takes (the calls are idempotent). Refusals are non-retryable (activities).
  // └──────────────────────────────────────────────────────────────────────────────────────
  retry: { initialInterval: '5 seconds', backoffCoefficient: 2, maximumInterval: '5 minutes' },
});

/**
 * The safety net: without a signal (event missed, Kafka or the listener down) the payment is
 * checked again after this long anyway.
 */
const FALLBACK_POLL_SECONDS = 5 * 60;
/** An order whose payment was never started (the counter stopped after ORDERED) is cancelled after this long. */
const NO_PAYMENT_CANCEL_SECONDS = 30 * 60;
/** Checks per run: then continue-as-new, so the history stays small even for a payment awaiting approval for days. */
const CHECKS_PER_RUN = 240;

const NOT_PAID = ['REJECTED', 'FAILED', 'COMPENSATION_FAILED'];

// ┌─ :: ARCHITECTURE + RESILIENCE :: The Digital Gold purchase workflow ───────────────────────────
// │ Why : After the counter placed the order and started the payment, the purchase must end in
// │       exactly one of two ways - gold allocated (the payment advice) or the order cancelled (no
// │       payment) - however long the payment takes and whatever restarts in between.
// │ How : Durable: Temporal keeps the workflow's state; the worker may stop and start. It watches
// │       the payment by the purchase's Idempotency-Key (EWP Payments runs its own saga: approval,
// │       funds, payment network); COMPLETED -> payment advice -> ALLOCATED; REJECTED / FAILED /
// │       COMPENSATION_FAILED -> cancel -> CANCELLED; no payment within 30 minutes -> cancel. The
// │       input is the purchase number only - no token, no account number in Temporal's history.
// │       It waits for the paymentSettled SIGNAL (payments.payment.events, via the listener) or, at
// │       the latest, 5 minutes - then looks again.
// │ Ref : Partner Products step 8 | Partner Bank repo D11 (5-minute price lock, re-quote) |
// │       Temporal: signals, durable timers, continue-as-new
// └────────────────────────────────────────────────────────────────────────────────────────────────
export async function goldPurchaseWorkflow(input: GoldPurchaseInput): Promise<GoldPurchaseOutcome> {
  const purchase = await purchaseState(input.purchaseNumber);
  if (purchase.status === 'ALLOCATED' || purchase.status === 'CANCELLED' || purchase.status === 'REFUSED') {
    return purchase.status;
  }

  // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
  // │ The signal only WAKES the workflow: what happens next is decided by the payment the
  // │ Payments API shows (by this purchase's Idempotency-Key). A repeated, late or forged
  // │ event costs one extra look - it can never allocate or cancel by itself.
  // └──────────────────────────────────────────────────────────────────────────────────────
  let nudged = false;
  setHandler(paymentSettledSignal, () => { nudged = true; });

  let waitedWithoutPayment = input.waitedWithoutPaymentSeconds ?? 0;
  for (let check = 0; check < CHECKS_PER_RUN; check++) {
    const payment = await paymentOutcome(purchase.paymentRef);

    // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
    // │ If the payment COMPLETED, send the payment advice (gold allocated). If it was not
    // │ made (REJECTED, FAILED, COMPENSATION_FAILED) or none was started in 30 minutes,
    // │ cancel the unpaid order. Anything else (approval, network) - wait and check again.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (payment?.status === 'COMPLETED') {
      const done = await sendPaymentAdvice(purchase.number, purchase.paymentRef, payment);
      log.info('Gold allocated', { purchase: purchase.number, payment: payment.paymentNumber });
      return done.status === 'ALLOCATED' ? 'ALLOCATED' : 'CANCELLED';
    }

    if (payment && NOT_PAID.includes(payment.status)) {
      await cancelOrder(purchase.number, `Payment ${payment.paymentNumber} was not made (${payment.status}).`);
      log.info('Order cancelled: not paid', { purchase: purchase.number, payment: payment.paymentNumber, status: payment.status });
      return 'CANCELLED';
    }

    if (!payment && waitedWithoutPayment >= NO_PAYMENT_CANCEL_SECONDS) {
      await cancelOrder(purchase.number, 'No payment was started for the order within 30 minutes.');
      return 'CANCELLED';
    }

    // Workflow time (Date.now() is deterministic here): how long this wait really took.
    const waitStarted = Date.now();
    await condition(() => nudged, `${FALLBACK_POLL_SECONDS} seconds`);
    nudged = false;
    if (!payment) waitedWithoutPayment += Math.round((Date.now() - waitStarted) / 1000);
  }

  return continueAsNew<typeof goldPurchaseWorkflow>({ purchaseNumber: input.purchaseNumber, waitedWithoutPaymentSeconds: waitedWithoutPayment });
}