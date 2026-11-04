// ┌─ :: ARCHITECTURE :: The listener's decision, apart from Kafka ────────────────────────────────
// │ Why : What makes a payment event worth passing on is the worker's side of its contract with
// │       the Payments API; it must be testable without loading the Kafka client's native binding.
// │ How : A pure function over the raw message; payment-events.ts only feeds it from Kafka. The
// │       contract test (test/contracts) drives this function with Pact's example messages.
// │ Ref : tst/contracts/README.md | Integration-Event-Catalogue 4.5
// └────────────────────────────────────────────────────────────────────────────────────────────────

/** A payment's final outcomes - the moments a waiting purchase workflow should look again. */
const OUTCOMES = new Set(['PaymentCompleted', 'PaymentRejected', 'PaymentFailed', 'PaymentCompensationFailed']);

interface PaymentEvent {
  EventType?: string;
  Payload?: { PaymentRef?: string; PaymentNumber?: string; ToBsb?: string; ToAccountNumber?: string };
}

/** A payment's final outcome that a waiting purchase workflow should hear about. */
export interface SettledPayment {
  eventType: string;
  paymentRef: string;
  paymentNumber: string | undefined;
}

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ Tolerant reader of the Payments API's envelope: only EventType and four payload fields
// │ are read; anything else may change freely. These are exactly the fields of the
// │ contract partner-products-worker-payments-api.json (test/contracts), which the
// │ Payments API's tests check against its real Outbox messages.
// └──────────────────────────────────────────────────────────────────────────────────────
/** The outcome to pass on, or null: not JSON, not a final outcome, or not a payment to the Partner Bank's settlement account. */
export function settledPaymentOf(raw: string | undefined, settlement: { bsb: string; accountNumber: string }): SettledPayment | null {
  if (!raw) return null;
  let event: PaymentEvent;
  try {
    event = JSON.parse(raw) as PaymentEvent;
  } catch {
    return null;   // not JSON: nothing to pass on
  }
  const payload = event.Payload;
  if (!event.EventType || !OUTCOMES.has(event.EventType) || !payload?.PaymentRef) return null;
  // Only payments to the Partner Bank's settlement account can belong to a purchase.
  if (payload.ToBsb !== settlement.bsb || payload.ToAccountNumber !== settlement.accountNumber) return null;
  if (!/^[0-9a-f-]{36}$/i.test(payload.PaymentRef)) return null;
  return { eventType: event.EventType, paymentRef: payload.PaymentRef, paymentNumber: payload.PaymentNumber };
}