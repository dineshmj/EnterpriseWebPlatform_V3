import * as fs from 'node:fs';
import { createServer } from 'node:https';
import { NativeConnection, Worker } from '@temporalio/worker';
import { createActivities } from './activities';
import { loadOptions } from './config';
import { PaymentEventListener } from './payment-events';

/**
 * Partner Products Worker - runs the Digital Gold purchase workflow on Temporal (task queue
 * "partner-products"). It holds no data: Temporal keeps the workflow's state, the Partner Products
 * API keeps the purchase. Start Temporal first ("temporal server start-dev").
 */
async function main() {
  // The console window's title, like the other services.
  process.title = 'Partner Products Worker';
  const options = loadOptions();

  const connection = await NativeConnection.connect({ address: options.temporalAddress });
  const worker = await Worker.create({
    connection,
    namespace: options.temporalNamespace,
    taskQueue: options.taskQueue,
    // Workflows are bundled and run in Temporal's deterministic sandbox; activities run here, in Node.
    workflowsPath: require.resolve('./workflows'),
    activities: createActivities(options),
  });

  // The payment-event listener runs beside the worker; until Kafka answers it tries again
  // every 30 s (the workflows still look at their payments every 5 minutes meanwhile).
  const listener = new PaymentEventListener(options);
  void (async () => {
    while (listener.state !== 'running') {
      try {
        await listener.start();
      } catch (error) {
        console.warn(`Payment-event listener not started (${(error as Error).message}); trying again in 30 s.`);
        await listener.stop();
        listener.state = 'starting';
        await new Promise((resolve) => setTimeout(resolve, 30_000));
      }
    }
  })();

  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ Probes for Operations -> Platform Health: live = the process, ready = the worker is
  // │ polling Temporal (the listener's state is shown, but a missing listener only slows
  // │ purchases down to the 5-minute look). Nothing else is served (no data, no actions).
  // └──────────────────────────────────────────────────────────────────────────────────────
  createServer({ pfx: fs.readFileSync(options.tlsPfxPath), passphrase: options.tlsPfxPassword }, (req, res) => {
    const ready = worker.getState() === 'RUNNING';
    const status = req.url === '/health/live' ? 200 : req.url === '/health/ready' || req.url === '/health' ? (ready ? 200 : 503) : 404;
    res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    res.end(status === 404 ? '{}' : JSON.stringify({ status: status === 200 ? 'Healthy' : 'Unhealthy', worker: worker.getState(), paymentEvents: listener.state }));
  }).listen(options.healthPort, '0.0.0.0');

  console.log(`Partner Products Worker polling ${options.temporalAddress} (${options.temporalNamespace}), task queue ${options.taskQueue}; health https://localhost:${options.healthPort}/health`);
  await worker.run();
  await listener.stop();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});