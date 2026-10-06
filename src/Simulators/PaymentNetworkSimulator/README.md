# Payment Network Simulator

Stands in for the **external payment network** - an NPP-style clearing service that moves the money to the payee's bank - so the Payments saga can show what happens when a third party is slow, failing, down or refusing: retries, the circuit breaker, and **compensation** (releasing the reserved funds) after a failure. It belongs to no bounded context; it keeps its payments in memory.

URL: `https://localhost:46386` (launch profile `https`). The Payments API's saga step runner calls it **directly over HTTP** (not through Kafka).

## API

| Endpoint | Who | Purpose |
|---|---|---|
| `POST /v1/payments` | Payments API, headers `X-Api-Key` (constant-time comparison) and **`Idempotency-Key`** | Send a payment: `201 { networkReference, status: "SETTLED" }`, or `422 { reason }` |
| `GET /admin/behaviour` | localhost only | Current behaviour |
| `PUT /admin/behaviour` | localhost only | Change behaviour at runtime: `{"behaviour":"Down"}` |
| `GET /health` | anyone | Liveness |

## Idempotency

The same `Idempotency-Key` always returns the **same** answer (the payment's network reference, or the same refusal), whatever the behaviour is now: the payment was already sent, only the answer was lost. The Payments API sends the PaymentRef as the key, which is what makes its retries safe - a payment can never be sent twice.

## Behaviours

| Behaviour | Response (new key) | What the saga does |
|---|---|---|
| `Healthy` | 201 | Settles the funds: payment COMPLETED |
| `Slow` | 201 after `SlowDelaySeconds` (12) | The 5-second attempt timeout fires: retried |
| `Failing` / `Down` | 500 / 503 | Retries with back-off; after 4 attempts, releases the funds: payment FAILED |
| `Refusing` | 422 `{ reason }` | A permanent "no": releases the funds at once: payment FAILED |

Whatever the behaviour, a **payee BSB starting with 999** is refused ("the payee's account is closed"), so one payment can show the compensation path without changing anything.

```powershell
$pns = 'https://localhost:46386/admin/behaviour'
Invoke-RestMethod $pns -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / Healthy
Invoke-RestMethod $pns
```

## Configuration

`Simulator:Behaviour` (start-up behaviour), `Simulator:SlowDelaySeconds`, and `Simulator:ApiKey` (Development value in `appsettings.Development.json`; it must match the Payments API's `PaymentNetwork:ApiKey`). The simulator refuses to start without an API key.