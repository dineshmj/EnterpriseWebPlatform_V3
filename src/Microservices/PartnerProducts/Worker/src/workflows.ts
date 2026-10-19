import { continueAsNew, log, proxyActivities, sleep } from '@temporalio/workflow';
import type { Activities } from './activities';

export interface GoldPurchaseInput {
  purchaseNumber: string;
  /** How long this purchase has waited for a payment to be started (carried over by continue-as-new). */
  waitedWithoutPaymentSeconds?: number;
}

export type GoldPurchaseOutcome = 'ALLOCATED' | 'CANCELLED' | 'REFUSED';

const { purchaseState, paymentOutcome, sendPaymentAdvice, cancelOrder } = proxyActivities<Activities>({
  startToCloseTimeout: '30 seconds',
  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ The Partner Bank, the IDP or an API may be down for a while: retry with back-off, for
  // │ as long as it takes (the calls are idempotent). Refusals are non-retryable (activities).
  // └──────────────────────────────────────────────────────────────────────────────────────
  retry: { initialInterval: '5 seconds', backoffCoefficient: 2, maximumInterval: '5 minutes' },
});

const POLL_SECONDS = 15;
/** An order whose payment was never started (the counter stopped after ORDERED) is cancelled after this long. */
const NO_PAYMENT_CANCEL_SECONDS = 30 * 60;
/** Polls per run: then continue-as-new, so the history stays small even for a payment awaiting approval for days. */
const POLLS_PER_RUN = 240;

const NOT_PAID = ['REJECTED', 'FAILED', 'COMPENSATION_FAILED'];

// ┌─ :: ARCHITECTURE :: The Digital Gold purchase workflow ────────────────────────────────────────
// │ Why : After the counter placed the order and started the payment, the purchase must end in
// │       exactly one of two ways - gold allocated (the payment advice) or the order cancelled (no
// │       payment) - however long the payment takes and whatever restarts in between.
// │ How : Durable: Temporal keeps the workflow's state; the worker may stop and start. It watches
// │       the payment by the purchase's Idempotency-Key (EWP Payments runs its own saga: approval,
// │       funds, payment network); COMPLETED -> payment advice -> ALLOCATED; REJECTED / FAILED /
// │       COMPENSATION_FAILED -> cancel -> CANCELLED; no payment within 30 minutes -> cancel. The
// │       input is the purchase number only - no token, no account number in Temporal's history.
// │ Ref : Partner Products step 8 | Partner Bank repo D11 (5-minute price lock, re-quote) |
// │       Temporal: durable timers, continue-as-new
// └────────────────────────────────────────────────────────────────────────────────────────────────
export async function goldPurchaseWorkflow(input: GoldPurchaseInput): Promise<GoldPurchaseOutcome> {
  const purchase = await purchaseState(input.purchaseNumber);
  if (purchase.status === 'ALLOCATED' || purchase.status === 'CANCELLED' || purchase.status === 'REFUSED') {
    return purchase.status;
  }

  let waitedWithoutPayment = input.waitedWithoutPaymentSeconds ?? 0;
  for (let poll = 0; poll < POLLS_PER_RUN; poll++) {
    const payment = await paymentOutcome(purchase.paymentRef);

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

    if (!payment) {
      if (waitedWithoutPayment >= NO_PAYMENT_CANCEL_SECONDS) {
        await cancelOrder(purchase.number, 'No payment was started for the order within 30 minutes.');
        return 'CANCELLED';
      }
      waitedWithoutPayment += POLL_SECONDS;
    }

    await sleep(`${POLL_SECONDS} seconds`);
  }

  return continueAsNew<typeof goldPurchaseWorkflow>({ purchaseNumber: input.purchaseNumber, waitedWithoutPaymentSeconds: waitedWithoutPayment });
}