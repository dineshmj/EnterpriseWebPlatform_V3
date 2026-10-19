# Partner Products Worker (Temporal)

Runs the **Digital Gold purchase workflow** (Temporal, TypeScript SDK). At the counter, "Send & pay" places the order at the Partner Bank and starts the payment at EWP Payments as the staff member; then nobody waits. This worker follows the payment and ends the purchase in exactly one way:

```
Partner Products Journey API --start "gold-purchase-<number>"--> Temporal (task queue partner-products)
                                                                     |
Partner Products Worker <--------------------------------------------+
    every 15 s:  Payments API  GET /internal/v1/payments/by-idempotency-key/{key}
        COMPLETED                       -> Partner Products API  POST /internal/v1/gold-purchases/{n}/payment-advice  -> ALLOCATED
        REJECTED / FAILED /
        COMPENSATION_FAILED             -> Partner Products API  POST /internal/v1/gold-purchases/{n}/cancel          -> CANCELLED
        no payment after 30 minutes     -> cancel                                                                     -> CANCELLED
        anything else (approval, network) -> wait; continue-as-new every 240 polls
```

The Partner Products API is EWP's adapter to the Partner Bank: it holds the mutual-TLS certificate and sends the advice or the cancellation; this worker holds no Partner Bank key and no data.

| | |
|---|---|
| Start | Temporal first: `temporal server start-dev` (UI http://localhost:8233); then `runnow.bat` (or `.\ps\run\Start-NodeServices.ps1 -Only PartnerProducts`) |
| Temporal | `localhost:7233`, namespace `default`, task queue `partner-products`, workflow `goldPurchaseWorkflow` |
| Health | https://localhost:46383/health/live, /health/ready (ready = polling Temporal) |
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

The development client secret is in `runnow.bat` (and the IDP's `appsettings.Development.json`); outside Development it comes from the environment or a secret store.