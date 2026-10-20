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

// ┌─ :: ARCHITECTURE :: Events wake the workflow, polling is only the safety net ──────────────────
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

  constructor(private readonly options: WorkerOptions, private readonly tokens = new MachineToken(options)) {}

  async start(): Promise<void> {
    this.temporal = await Connection.connect({ address: this.options.temporalAddress });
    const client = new Client({ connection: this.temporal, namespace: this.options.temporalNamespace });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ SASL/SCRAM-SHA-512 as the worker's own Kafka user (development: SASL_PLAINTEXT on
    // │ localhost; production SASL_SSL). Starts at the latest offset: a past outcome needs no
    // │ signal - the workflow's own check on start-up reads it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    const kafka = new KafkaJS.Kafka({
      kafkaJS: {
        brokers: this.options.kafkaBootstrapServers.split(',').map((b) => b.trim()),
        clientId: 'partner-products-worker',
        ssl: false,
        sasl: { mechanism: 'scram-sha-512', username: this.options.kafkaUsername, password: this.options.kafkaPassword },
      },
    });
    this.consumer = kafka.consumer({ kafkaJS: { groupId: GROUP_ID, fromBeginning: false, autoCommit: true } });
    await this.consumer.connect();
    await this.consumer.subscribe({ topics: [TOPIC] });
    await this.consumer.run({
      eachMessage: async ({ message }) => {
        try {
          await this.handle(client, message.value?.toString('utf8'));
        } catch (error) {
          // Never stop the stream for one message: the workflow's fallback look covers it.
          console.warn(`Payment event not passed on (the workflow looks again within 5 minutes): ${(error as Error).message}`);
        }
      },
    });
    this.state = 'running';
    console.log(`Listening to ${TOPIC} (group ${GROUP_ID}) for Digital Gold payments.`);
  }

  private async handle(client: Client, raw: string | undefined): Promise<void> {
    if (!raw) return;
    let event: PaymentEvent;
    try {
      event = JSON.parse(raw) as PaymentEvent;
    } catch {
      return;   // not JSON: nothing to pass on
    }
    const payload = event.Payload;
    if (!event.EventType || !OUTCOMES.has(event.EventType) || !payload?.PaymentRef) return;
    // Only payments to the Partner Bank's settlement account can belong to a purchase.
    if (payload.ToBsb !== this.options.settlementBsb || payload.ToAccountNumber !== this.options.settlementAccountNumber) return;
    if (!/^[0-9a-f-]{36}$/i.test(payload.PaymentRef)) return;

    const purchaseNumber = await this.purchaseOf(payload.PaymentRef);
    if (!purchaseNumber) return;   // a payment to the Partner Bank that no purchase started

    try {
      await client.workflow.getHandle(`gold-purchase-${purchaseNumber}`).signal(paymentSettledSignal);
      console.log(`${event.EventType} ${payload.PaymentNumber ?? ''}: purchase ${purchaseNumber}'s workflow told to look again.`);
    } catch (error) {
      if (error instanceof WorkflowNotFoundError) return;   // already ended (or not started yet: it checks on start)
      throw error;
    }
  }

  /** The purchase that started this payment (its Idempotency-Key), from the Partner Products API. */
  private async purchaseOf(paymentRef: string): Promise<string | null> {
    const scope = 'partner-products.write';
    const response = await fetch(
      `${this.options.partnerProductsApiBaseUrl}/internal/v1/gold-purchases/by-payment-ref/${encodeURIComponent(paymentRef)}`,
      { headers: { Authorization: `Bearer ${await this.tokens.for(scope)}`, Accept: 'application/json' }, signal: AbortSignal.timeout(10_000) });
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