import { Inject, Injectable, Logger, OnModuleDestroy } from '@nestjs/common';
import { Client, Connection, WorkflowExecutionAlreadyStartedError } from '@temporalio/client';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';

/** Whether the purchase's follow-up (the Temporal workflow) is running. */
export type FollowUp = 'STARTED' | 'ALREADY_RUNNING' | 'NOT_STARTED';

// ┌─ :: ARCHITECTURE + RESILIENCE :: The purchase's follow-up runs as a Temporal workflow ─────────
// │ Why : Paying for gold is a long-running process across three systems (EWP Payments, the payment
// │       network, the Partner Bank): an approval or a slow network can take hours, and nobody waits
// │       at the counter. It must survive restarts and never advise or cancel twice.
// │ How : The Journey API only STARTS the workflow - workflow ID gold-purchase-<number>, so a second
// │       start is "already running", never a second workflow. Its input is the purchase number
// │       only: no token, no account number in the workflow's history. The Partner Products Worker
// │       runs it with its own machine identity. If Temporal cannot be reached, the purchase stays
// │       as it is and the screen offers "Continue" (this start again).
// │ Ref : Partner Products step 8 | Temporal workflow ID uniqueness | EWP roadmap (Temporal)
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class PurchaseWorkflowStarter implements OnModuleDestroy {
  private readonly logger = new Logger(PurchaseWorkflowStarter.name);
  private connection?: Promise<Connection>;

  constructor(@Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions) {}

  async ensureStarted(purchaseNumber: string): Promise<FollowUp> {
    try {
      const client = new Client({ connection: await this.connect(), namespace: this.options.temporalNamespace });
      await client.workflow.start('goldPurchaseWorkflow', {
        workflowId: `gold-purchase-${purchaseNumber}`,
        taskQueue: this.options.temporalTaskQueue,
        args: [{ purchaseNumber }],
      });
      this.logger.log(`Purchase workflow started for ${purchaseNumber}.`);
      return 'STARTED';
    } catch (error) {
      // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
      // │ If the workflow already runs, that is the answer (one per purchase, never two). If
      // │ Temporal cannot be reached, nothing is lost: the purchase stays as it is and the
      // │ screen offers "Continue", which starts it again.
      // └──────────────────────────────────────────────────────────────────────────────────────
      if (error instanceof WorkflowExecutionAlreadyStartedError) return 'ALREADY_RUNNING';
      this.logger.warn(`Purchase workflow for ${purchaseNumber} not started: ${(error as Error).message}`);
      this.connection = undefined;   // connect afresh next time (Temporal may have been down)
      return 'NOT_STARTED';
    }
  }

  async onModuleDestroy() {
    await (await this.connection?.catch(() => undefined))?.close();
  }

  private connect(): Promise<Connection> {
    this.connection ??= Connection.connect({ address: this.options.temporalAddress, connectTimeout: '5 seconds' });
    return this.connection;
  }
}