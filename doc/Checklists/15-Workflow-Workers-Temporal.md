# 15 — Workflow workers (Temporal)

Deployables: `src/Microservices/PartnerProducts/Worker` (the Digital Gold purchase workflow) · Runtime: Temporal (`ps\temporal\Start-Temporal.ps1`, development server)

## 1. Identity

- [x] **Its own machine identity** (client credentials, `PartnerProducts.Worker.M2M.ClientID`), never a stored person's token. *Because* the workflow runs long after the staff member's token has expired, and a machine must not act as a person. → `src/machine-token.ts`
- [x] **One token per API** (`payments.read` for Payments, `partner-products.write` for Partner Products), kept until a minute before it expires; a 401 drops it. *Because* a token for one API must never reach another.
- [x] **Accepted only where it is pinned:** Payments `GET /internal/v1/payments/by-idempotency-key/{key}` and Partner Products `/internal/v1/gold-purchases/...`; the Partner Products policy also refuses any token with a person (`sub` or `act`) in it. Staff endpoints need a person's permission and branch, which this token never has. → `PaymentOutcomeForPartnerProducts` (Payments `Program.cs`), `Worker` in `DigitalGoldPolicies.cs`

## 2. The workflow

- [x] **Its input is a business reference only** (the purchase number): no token, no account number, no amount in Temporal's history. *Because* the history is stored and shown in the Temporal UI. → `src/workflows.ts`
- [x] **Deterministic:** every call to another system is an activity; the workflow code imports no Node.js module (bundled and checked by the SDK at start-up). → `src/workflows.ts`, `src/activities.ts`
- [x] **Exactly one ending:** payment advice only for the purchase's own COMPLETED payment; cancellation only of an unpaid order (REJECTED, FAILED, COMPENSATION_FAILED, or no payment within 30 minutes). The APIs check again (key, amount, payee; PAID saved before the advice; a paid purchase never cancelled). *Because* gold must never be allocated without payment, nor a paid order cancelled.
- [x] **Started once:** workflow ID `gold-purchase-<number>`; a second start is "already running". → Journey API `purchase-workflow.starter.ts`
- [x] **Bounded history:** continue-as-new after 240 checks, so a payment waiting days for approval does not grow it for ever.

## 3. Failures

- [x] **Outages are retried with back-off** (5 s doubling to 5 minutes, without limit: the calls are idempotent); **refusals are final** (400 / 403 / 409 / 404: non-retryable, the workflow stops and Temporal shows why; the reason is kept on the purchase). → `call` in `src/activities.ts`
- [x] **Every call has a timeout** (20 s per HTTP call, 30 s per activity, 10 s for the token).
- [x] **A restart of the worker loses nothing:** Temporal keeps the state; the workflow continues where it was.

## 4. Configuration and operations

- [x] **Fail closed:** every address must be https, every setting is required, no secret has a default. → `src/config.ts`
- [x] **Health probe:** `https://localhost:46383/health/live` (the process) and `/health/ready` (polling Temporal); listed on Platform Health. → `src/main.ts`
- [x] **Install scripts not run** for the SDK's dependencies (`protobufjs`, `@swc/core`: notices and a binary check only); the ONE allowed is `@confluentinc/kafka-javascript` (fetches its prebuilt librdkafka binding); `pnpm audit` clean. → `pnpm-workspace.yaml`

## 5. Production (open)

- [ ] **Temporal secured:** mTLS to the frontend and namespace authorization (today the local development server, localhost only; production: mTLS from the service mesh or Temporal Cloud, not built in the app; R-16).
- [x] **Events instead of polling:** the worker listens to `payments.payment.events` (its own Kafka user, read-only) and signals the purchase's workflow; the signal carries nothing - the workflow reads the payment itself; fallback look every 5 minutes. → `src/payment-events.ts`, `src/workflows.ts`
- [ ] **Secret store** for the client secret (R-10).
- [ ] **Workflow metrics and alerts** (failed or long-running workflows) in the deployment's monitoring.