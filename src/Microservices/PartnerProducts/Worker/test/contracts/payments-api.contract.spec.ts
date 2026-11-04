import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { MatchersV3, PactV4 } from '@pact-foundation/pact';
import { createActivities } from '../../src/activities';
import type { WorkerOptions } from '../../src/config';
import type { MachineToken } from '../../src/machine-token';
import { settledPaymentOf } from '../../src/payment-outcome';

const { like, number, regex } = MatchersV3;

// SEC: Pact's native core reports usage to the Pact Foundation unless told not to - nothing from a test run leaves the machine.
process.env.PACT_DO_NOT_TRACK = 'true';

// ┌─ :: ARCHITECTURE :: Contract: what the worker needs from the Payments API ─────────────────────
// │ Why : The worker allocates or cancels gold on what Payments tells it - the payment events on
// │       payments.payment.events (Kafka) and the outcome read by the purchase's key (HTTP). A field
// │       Payments renamed would leave every purchase waiting, found only in production; and it is
// │       a different language (C#), so no shared type could catch it.
// │ How : The REAL reader (settledPaymentOf) gets each event as the contract states it; the REAL
// │       activity (paymentOutcome) calls Pact's mock server. Pact writes the contract
// │       partner-products-worker-payments-api.json (V4: HTTP and messages in one file); the Payments
// │       API's tests replay it against its real Outbox mapper and controller
// │       (tst/dotnet/Payments.Api.Tests/Contracts). Only what the worker READS is stated.
// │ Ref : tst/contracts/README.md | Integration-Event-Catalogue 4.5 | Pact V4
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('Contract with the Payments API (partner-products-worker -> payments-api)', () => {
  const PAYMENT_REF = '0199f2a4-6c1e-7b3a-9d42-5e8f1a2b3c4d';
  const GUID = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
  const SETTLEMENT = { bsb: '033-000', accountNumber: '10203040' };

  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ Written into an empty folder; only a run in which EVERY contract test passed replaces
  // │ the committed file in tst/contracts/pacts (one atomic rename, no final newline). A
  // │ filtered (-t) or failed run leaves it alone; CI compares the folder with the commit.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const workDir = mkdtempSync(join(tmpdir(), 'ewp-pacts-'));
  let registered = 0;
  let passed = 0;
  const contract = (name: string, test: () => Promise<void>) => {
    registered++;
    it(name, async () => {
      // The interactions' own expectations live in `test`; this asserts it ran to the end.
      await expect(test()).resolves.toBeUndefined();
      passed++;
    });
  };

  const pact = new PactV4({ consumer: 'partner-products-worker', provider: 'payments-api', dir: workDir, logLevel: 'warn' });

  afterAll(() => {
    if (registered > 0 && passed === registered) {
      const target = contractsFolder();
      mkdirSync(target, { recursive: true });
      for (const file of readdirSync(workDir).filter((f) => f.endsWith('.json'))) {
        const staged = join(target, `${file}.tmp`);
        writeFileSync(staged, readFileSync(join(workDir, file), 'utf8').trimEnd());
        renameSync(staged, join(target, file));
      }
    } else {
      console.warn(`Contracts not published: ${passed} of ${registered} contract tests passed. Run every test to update tst/contracts/pacts.`);
    }
    rmSync(workDir, { recursive: true, force: true });
  });

  describe('payment events (payments.payment.events)', () => {
    for (const eventType of ['PaymentCompleted', 'PaymentFailed']) {
      contract(`Given a ${eventType} event for a payment to the settlement account, when the listener reads it, then it passes the payment on`, async () => {
        await pact
          .addAsynchronousInteraction()
          .expectsToReceive(`a ${eventType} event`, (message) => message.withJSONContent({
            EventType: eventType,
            Payload: {
              PaymentRef: regex(GUID, PAYMENT_REF),
              PaymentNumber: like('PAY-261015-TEST01'),
              ToBsb: like(SETTLEMENT.bsb),
              ToAccountNumber: like(SETTLEMENT.accountNumber),
            },
          }))
          .executeTest(async (message) => {
            // When
            const payment = settledPaymentOf(JSON.stringify(message.contents.content), SETTLEMENT);

            // Then
            expect(payment).toEqual({ eventType, paymentRef: PAYMENT_REF, paymentNumber: 'PAY-261015-TEST01' });
          });
      });
    }
  });

  describe('the payment outcome (GET /internal/v1/payments/by-idempotency-key/{paymentRef})', () => {
    const path = `/internal/v1/payments/by-idempotency-key/${PAYMENT_REF}`;

    contract('Given a completed payment for the purchase key, when the workflow reads it, then it gets what the payment advice needs', async () => {
      await pact
        .addInteraction()
        .given('a completed payment exists for the key')
        .uponReceiving('a request for the outcome of a payment')
        .withRequest('GET', path, (request) => request.headers({ Accept: 'application/json' }))
        .willRespondWith(200, (response) => response.jsonBody({
          paymentNumber: like('PAY-261015-TEST01'),
          // CONTRACT: the exact code - the workflow allocates the gold only on COMPLETED.
          status: 'COMPLETED',
          amount: number(140.25),
          payeeBsb: like(SETTLEMENT.bsb),
          payeeAccountNumber: like(SETTLEMENT.accountNumber),
          networkReference: like('NPP-261015-000042'),
        }))
        .executeTest(async (mockServer) => {
          // When
          const payment = await activitiesAt(mockServer.url).paymentOutcome(PAYMENT_REF);

          // Then
          expect(payment).toMatchObject({ status: 'COMPLETED', amount: 140.25, networkReference: 'NPP-261015-000042' });
        });
    });

    contract('Given no payment was started with the purchase key, when the workflow reads it, then it gets none (404, not an error)', async () => {
      await pact
        .addInteraction()
        .given('no payment was started with the key')
        .uponReceiving('a request for the outcome of a payment never started')
        .withRequest('GET', path, (request) => request.headers({ Accept: 'application/json' }))
        .willRespondWith(404)
        .executeTest(async (mockServer) => {
          // When
          const payment = await activitiesAt(mockServer.url).paymentOutcome(PAYMENT_REF);

          // Then
          expect(payment).toBeNull();
        });
    });
  });

  /** The REAL activities against the mock server; the machine token is a stand-in (DPoP is proven by machine-token.spec.ts). */
  function activitiesAt(paymentsApiBaseUrl: string) {
    const tokens = {
      authorization: async () => ({ Authorization: 'DPoP contract-test', DPoP: 'contract-test-proof' }),
      forget: () => undefined,
    } as unknown as MachineToken;
    return createActivities({ paymentsApiBaseUrl, partnerProductsApiBaseUrl: 'https://partner-products.ewp.example' } as WorkerOptions, tokens);
  }

  /** tst/contracts/pacts, found from here upwards (the repository root holds EnterpriseWebPlatform.BSS.slnx). */
  function contractsFolder(): string {
    for (let folder = __dirname; ; folder = dirname(folder)) {
      if (existsSync(join(folder, 'EnterpriseWebPlatform.BSS.slnx'))) return join(folder, 'tst', 'contracts', 'pacts');
      if (dirname(folder) === folder) throw new Error(`The repository root was not found above ${__dirname}.`);
    }
  }
});