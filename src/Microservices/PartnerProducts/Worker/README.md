# Partner Products Worker (Temporal)

Runs the **Digital Gold purchase workflow** (Temporal, TypeScript SDK). At the counter, "Send & pay" places the order at the Partner Bank and starts the payment at EWP Payments as the staff member; then nobody waits. This worker follows the payment and ends the purchase in exactly one way:

```
Partner Products Journey API --start "gold-purchase-<number>"--> Temporal (task queue partner-products)
                                                                     |
Partner Products Worker <--------------------------------------------+
    Kafka payments.payment.events (a final outcome, payee = the Partner Bank's settlement account)
        -> Partner Products API  GET /internal/v1/gold-purchases/by-payment-ref/{key}  -> signal paymentSettled
    on that signal, or every 5 minutes at the latest:
                 Payments API  GET /internal/v1/payments/by-idempotency-key/{key}
        COMPLETED                       -> Partner Products API  POST /internal/v1/gold-purchases/{n}/payment-advice  -> ALLOCATED
        REJECTED / FAILED /
        COMPENSATION_FAILED             -> Partner Products API  POST /internal/v1/gold-purchases/{n}/cancel          -> CANCELLED
        no payment after 30 minutes     -> cancel                                                                     -> CANCELLED
        anything else (approval, network) -> wait; continue-as-new every 240 checks
```

The Partner Products API is EWP's adapter to the Partner Bank: it holds the mutual-TLS certificate and sends the advice or the cancellation; this worker holds no Partner Bank key and no data.

| | |
|---|---|
| Start | Temporal first: `temporal server start-dev` (UI http://localhost:8233); then `runnow.bat` (or `.\ps\run\Start-NodeServices.ps1 -Only PartnerProducts`) |
| Temporal | `localhost:7233`, namespace `default`, task queue `partner-products`, workflow `goldPurchaseWorkflow` |
| Health | https://localhost:46383/health/live, /health/ready (ready = polling Temporal; `paymentEvents` shows the listener: starting / running) |
| Kafka | Consumer group `partner-products.worker` on `payments.payment.events`, user `ewp-partner-products-worker` (SCRAM-SHA-512 over TLS, CA `KAFKA_SSL_CA_LOCATION`), Confluent's `@confluentinc/kafka-javascript` |
| Identity | Machine client `PartnerProducts.Worker.M2M.ClientID` (client credentials): `payments.read`, `partner-products.write` |

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Its own machine identity (client credentials), one token per API (`payments.read` for Payments, `partner-products.write` for Partner Products); both APIs accept it only on internal endpoints that pin its client ID - never on a staff endpoint | The worker would need a person's token (expired long before the payment ends) or would get a staff member's reach | Broken function-level authorization (OWASP API5); token replay across APIs | [machine-token.ts](src/machine-token.ts) |
| 2 | The workflow's input is the purchase number only: no token, no account number, no amount in Temporal's history; it reads what it needs through the APIs | Credentials or personal data stored in workflow history, readable in the Temporal UI | Credential and data exposure | [workflows.ts](src/workflows.ts) |
| 3 | Exactly one ending per purchase: allocate only on a COMPLETED payment, cancel only on a payment that was not made (or none at all); the APIs re-check (key, amount, payee; never cancel a paid order) and are idempotent, so retries after a lost answer allocate or cancel once | Gold allocated for an unpaid purchase, a paid order cancelled, or a double allocation | Financial loss; inconsistent state | [workflows.ts](src/workflows.ts), [activities.ts](src/activities.ts) |
| 4 | Refusals (400 / 403 / 409 / 404) stop the workflow (non-retryable, visible in the Temporal UI, the reason kept on the purchase); outages are retried with back-off | A refusal retried for ever; an outage turned into a wrong ending | Inconsistent state | `call` in [activities.ts](src/activities.ts) |
| 5 | Fail-closed configuration: every address https, no secret defaults | Bearer tokens sent in the clear; a half-configured worker | Misconfiguration (OWASP A05) | [config.ts](src/config.ts) |
| 6 | The health probe serves only its two answers over https (`nosniff`, `no-store`) | A probe that leaks or does more | Information disclosure | [main.ts](src/main.ts) |
| 7 | A payment event only WAKES the workflow (signal without data); the workflow decides from the payment the Payments API shows by the purchase's own key - so no Inbox is needed, and a repeated, late or forged event costs one extra look | An event on the topic could allocate gold or cancel an order by itself | Forged or replayed messages (spoofing, tampering) | [signals.ts](src/signals.ts), [workflows.ts](src/workflows.ts) |
| 8 | Its own Kafka user over TLS (SASL_SSL; the broker's certificate and host name checked, a configured CA file that is missing stops the start-up), READ on `payments.payment.events` and its group only; only final outcomes to the settlement account are looked up, by an internal endpoint pinned to this worker | A broad Kafka identity; a lookup for every payment of the bank | Excessive privilege; data exposure | [payment-events.ts](src/payment-events.ts), `Setup-KafkaSecurity.ps1` |
| 9 | Polling stays as the safety net (every 5 minutes): Kafka down, the listener stopped or an event missed only slow a purchase down | A missed event leaves a paid purchase unallocated | Lost update; availability | [workflows.ts](src/workflows.ts) |
| 10 | Container image: CI builds the worker from the committed lockfile (`--frozen-lockfile`) - never a local build; production dependencies only; slim Node runtime with the published Debian security updates applied at build time and without npm, npx, corepack or yarn, as the non-root user 1000 with its files read-only to it; scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); no secret inside - every setting comes from the environment, and `runnow.bat` and key material never enter the build (the Kafka CA file is mounted at run time) | A developer-built image could carry anything; a package manager in a container helps an attacker who gets in; a secret in a layer stays in the registry for good | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 11 | TLS here or at the platform, never neither by accident: `PARTNER_PRODUCTS_WORKER_PLAIN_HTTP=true` (the image sets it) listens on plain HTTP 8080 - for the health probes only; otherwise the development certificate (`PARTNER_PRODUCTS_WORKER_TLS_PFX_PATH`) is required, and both set refuse to start | A missing setting could quietly open plain HTTP | Misconfiguration (OWASP A05); cleartext transmission (CWE-319) | [listener-tls.ts](src/listener-tls.ts) |

The development client secret is in `runnow.bat` (and the IDP's `appsettings.Development.json`); outside Development it comes from the environment or a secret store.