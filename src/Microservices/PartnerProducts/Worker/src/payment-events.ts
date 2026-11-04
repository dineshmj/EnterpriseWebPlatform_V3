import { KafkaJS } from '@confluentinc/kafka-javascript';
import { Client, Connection, WorkflowNotFoundError } from '@temporalio/client';
import type { WorkerOptions } from './config';
import { MachineToken } from './machine-token';
import { paymentSettledSignal } from './signals';

const TOPIC = 'payments.payment.events';
/** The Kafka ACLs grant the worker's user READ on this group only. */
const GROUP_ID = 'partner-products.worker';
/** A payment's final outcomes - the moments a waiting purchase workflow should look again. */
const OUTCOMES = new Set(['PaymentCompleted', 'PaymentRejected', 'PaymentFailed', 'PaymentCompensationFailed']);

export type ListenerState = 'starting' | 'running' | 'stopped';

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

// ┌─ :: RESILIENCE :: Events wake the workflow, polling is only the safety net ────────────────────
// │ Why : A purchase should be allocated (or cancelled) as soon as its payment has an outcome, without
// │       a stream of status calls to EWP Payments for every open purchase.
// │ How : Consumer group partner-products.worker reads payments.payment.events (its own Kafka user,
// │       read-only). A final outcome for a payment to the Partner Bank's settlement account -> the
// │       purchase is found by the payment's key (Partner Products API, internal endpoint pinned to
// │       this worker) -> its workflow gets the paymentSettled signal. Anything that goes wrong here is
// │       only logged: the workflow looks again within 5 minutes anyway.
// │ Ref : Temporal signals | Integration-Event-Catalogue 4.5 | Threat-Model R-16
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class PaymentEventListener {
  private consumer: KafkaJS.Consumer | null = null;
  private temporal: Connection | null = null;
  state: ListenerState = 'starting';

  constructor(private readonly options: WorkerOptions, private readonly tokens: MachineToken) {}

  async start(): Promise<void> {
    this.temporal = await Connection.connect({ address: this.options.temporalAddress });
    const client = new Client({ connection: this.temporal, namespace: this.options.temporalNamespace });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ SASL_SSL everywhere (localhost included): TLS with the broker's certificate checked
    // │ against the configured CA and its host name, then SCRAM-SHA-512 as the worker's own
    // │ Kafka user. Starts at the latest offset: a past outcome needs no signal - the
    // │ workflow's own check on start-up reads it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    const kafka = new KafkaJS.Kafka({
      kafkaJS: {
        brokers: this.options.kafkaBootstrapServers.split(',').map((b) => b.trim()),
        clientId: 'partner-products-worker',
        ssl: true,
        sasl: { mechanism: 'scram-sha-512', username: this.options.kafkaUsername, password: this.options.kafkaPassword },
      },
      ...(this.options.kafkaSslCaLocation ? { 'ssl.ca.location': this.options.kafkaSslCaLocation } : {}),
      'ssl.endpoint.identification.algorithm': 'https',
    });
    this.consumer = kafka.consumer({ kafkaJS: { groupId: GROUP_ID, fromBeginning: false, autoCommit: true } });
    await this.consumer.connect();
    await this.consumer.subscribe({ topics: [TOPIC] });
    await this.consumer.run({
      eachMessage: async ({ message }) => {
        try {
          await this.handle(client, message.value?.toString('utf8'));
        } catch (error) {
          // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
          // │ Never stop the stream for one message: the workflow's fallback look covers it.
          // └──────────────────────────────────────────────────────────────────────────────────────
          console.warn(`Payment event not passed on (the workflow looks again within 5 minutes): ${(error as Error).message}`);
        }
      },
    });
    this.state = 'running';
    console.log(`Listening to ${TOPIC} (group ${GROUP_ID}) for Digital Gold payments.`);
  }

  private async handle(client: Client, raw: string | undefined): Promise<void> {
    const payment = settledPaymentOf(raw, { bsb: this.options.settlementBsb, accountNumber: this.options.settlementAccountNumber });
    if (!payment) return;

    const purchaseNumber = await this.purchaseOf(payment.paymentRef);
    if (!purchaseNumber) return;   // a payment to the Partner Bank that no purchase started

    try {
      await client.workflow.getHandle(`gold-purchase-${purchaseNumber}`).signal(paymentSettledSignal);
      console.log(`${payment.eventType} ${payment.paymentNumber ?? ''}: purchase ${purchaseNumber}'s workflow told to look again.`);
    } catch (error) {
      // Already ended (e.g. the 5-minute check got there first after a restart) or not started
      // yet (it reads the payment on start): nothing to do - said once, so a log reads clearly.
      if (error instanceof WorkflowNotFoundError) {
        console.log(`${payment.eventType} ${payment.paymentNumber ?? ''}: purchase ${purchaseNumber}'s workflow is not running (already ended, or not started yet) - nothing to tell it.`);
        return;
      }
      throw error;
    }
  }

  /** The purchase that started this payment (its Idempotency-Key), from the Partner Products API. */
  private async purchaseOf(paymentRef: string): Promise<string | null> {
    const scope = 'partner-products.write';
    const url = `${this.options.partnerProductsApiBaseUrl}/internal/v1/gold-purchases/by-payment-ref/${encodeURIComponent(paymentRef)}`;
    const response = await fetch(url,
      { headers: { ...await this.tokens.authorization(scope, 'GET', url), Accept: 'application/json' }, signal: AbortSignal.timeout(10_000) });
    if (response.status === 404) return null;
    if (response.status === 401) this.tokens.forget(scope);
    if (!response.ok) throw new Error(`HTTP ${response.status} looking up the purchase of a payment`);
    const purchase = (await response.json()) as { number?: string };
    return typeof purchase.number === 'string' && /^DGP-[A-Z0-9-]{1,26}$/.test(purchase.number) ? purchase.number : null;
  }

  async stop(): Promise<void> {
    this.state = 'stopped';
    await this.consumer?.disconnect().catch(() => undefined);
    await this.temporal?.close().catch(() => undefined);
  }
}