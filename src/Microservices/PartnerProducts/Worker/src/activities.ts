import { ApplicationFailure } from '@temporalio/common';
import type { WorkerOptions } from './config';
import { MachineToken } from './machine-token';

/** The purchase as the Partner Products API's internal endpoint shows it. */
export interface PurchaseState {
  number: string;
  status: 'RECORDED' | 'SENT' | 'ORDERED' | 'PAID' | 'ALLOCATED' | 'REFUSED' | 'CANCELLED';
  amountAud: number;
  paymentRef: string;
  partnerOrderNumber: string | null;
  paymentNumber: string | null;
}

/** A payment's outcome as the Payments API's internal endpoint shows it. */
export interface PaymentOutcome {
  paymentNumber: string;
  status: string;
  amount: number;
  payeeBsb: string;
  payeeAccountNumber: string;
  reference: string | null;
  networkReference: string | null;
}

const PAYMENTS_READ = 'payments.read';
const PARTNER_PRODUCTS_WRITE = 'partner-products.write';

// ┌─ :: ARCHITECTURE :: Activities: the workflow's only contact with the outside ──────────────────
// │ Why : A workflow must be deterministic (Temporal replays it); every call to another system is
// │       an activity, whose result Temporal records once.
// │ How : Four small HTTP calls with the worker's machine token. What a retry cannot fix (a 404, or a
// │       409 refusal: the purchase is in another state, the Partner Bank said no) is a NON-retryable
// │       failure - the workflow stops and Temporal shows why; everything else (503, timeouts, the
// │       IDP restarting) is retried by Temporal with back-off. The endpoints are idempotent, so a
// │       retry after a lost answer allocates or cancels once.
// │ Ref : Temporal activities and retry policies | Partner Products step 8
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function createActivities(options: WorkerOptions) {
  const tokens = new MachineToken(options);
  const purchases = `${options.partnerProductsApiBaseUrl}/internal/v1/gold-purchases`;

  async function call<T>(scope: string, method: 'GET' | 'POST', url: string, body?: unknown): Promise<T | null> {
    const response = await fetch(url, {
      method,
      headers: {
        Authorization: `Bearer ${await tokens.for(scope)}`,
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(20_000),
    });
    if (response.ok) return (await response.json()) as T;
    if (response.status === 404) return null;

    const problem = (await response.json().catch(() => ({}))) as { detail?: string };
    if (response.status === 401) tokens.forget(scope);
    if (response.status === 400 || response.status === 403 || response.status === 409) {
      throw ApplicationFailure.nonRetryable(problem.detail ?? `HTTP ${response.status} from ${method} ${url}`, `Refused${response.status}`);
    }
    throw new Error(problem.detail ?? `HTTP ${response.status} from ${method} ${url}`);   // retried by Temporal
  }

  return {
    async purchaseState(purchaseNumber: string): Promise<PurchaseState> {
      const state = await call<PurchaseState>(PARTNER_PRODUCTS_WRITE, 'GET', `${purchases}/${encodeURIComponent(purchaseNumber)}`);
      if (!state) throw ApplicationFailure.nonRetryable(`Purchase ${purchaseNumber} does not exist.`, 'NotFound');
      return state;
    },

    /** The payment by its Idempotency-Key; null while no payment was started with it. */
    async paymentOutcome(paymentRef: string): Promise<PaymentOutcome | null> {
      return call<PaymentOutcome>(PAYMENTS_READ, 'GET',
        `${options.paymentsApiBaseUrl}/internal/v1/payments/by-idempotency-key/${encodeURIComponent(paymentRef)}`);
    },

    async sendPaymentAdvice(purchaseNumber: string, paymentRef: string, payment: PaymentOutcome): Promise<PurchaseState> {
      const state = await call<PurchaseState>(PARTNER_PRODUCTS_WRITE, 'POST', `${purchases}/${encodeURIComponent(purchaseNumber)}/payment-advice`, {
        paymentRef,
        networkReference: payment.networkReference,
        amount: payment.amount,
        payeeBsb: payment.payeeBsb,
        payeeAccountNumber: payment.payeeAccountNumber,
      });
      if (!state) throw ApplicationFailure.nonRetryable(`Purchase ${purchaseNumber} does not exist.`, 'NotFound');
      return state;
    },

    async cancelOrder(purchaseNumber: string, reason: string): Promise<PurchaseState> {
      const state = await call<PurchaseState>(PARTNER_PRODUCTS_WRITE, 'POST', `${purchases}/${encodeURIComponent(purchaseNumber)}/cancel`, { reason });
      if (!state) throw ApplicationFailure.nonRetryable(`Purchase ${purchaseNumber} does not exist.`, 'NotFound');
      return state;
    },
  };
}

export type Activities = ReturnType<typeof createActivities>;