# Payment Network Simulator

Stands in for the **external payment network** - an NPP-style clearing service that moves the money to the payee's bank - so the Payments saga can show what happens when a third party is slow, failing, down or refusing: retries, the circuit breaker, and **compensation** (releasing the reserved funds) after a failure. It belongs to no bounded context; it keeps its payments in memory.

URL: `https://localhost:46386` (launch profile `https`). The Payments API's saga step runner calls it **directly over HTTP** (not through Kafka).

## API

| Endpoint | Who | Purpose |
|---|---|---|
| `POST /v1/payments` | Payments API, headers `X-Api-Key` (constant-time comparison) and **`Idempotency-Key`** | Send a payment: `201 { networkReference, status: "SETTLED" }`, or `422 { reason }` |
| `GET /v1/bsb/{bsb}` | Payments API, `X-Api-Key` | BSB directory: `{ bsb, bank, branch, state, npp }`; 404 when unknown |
| `POST /v1/payee-confirmations` | Payments API, `X-Api-Key` | Confirmation of Payee: `{ result: MATCH / CLOSE_MATCH / NO_MATCH, accountNameHeld }` |
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

Whatever the behaviour, a **payee BSB starting with 999** is refused ("the payee's account is closed"), so one payment can show the compensation path without changing anything. A BSB that is not in the directory is refused too.

## BSB directory and Confirmation of Payee (demo rules)

- **BSB directory:** the first two digits name the bank (01 ANZ, 03 / 73 Westpac, 06 Commonwealth Bank, 08 NAB, 11 St.George, 18 Macquarie, 48 Suncorp, 63 Bendigo, 80 Cuscal, 99 "Closed Bank (demo)"), the third the state. Anything else is unknown.
- **Confirmation of Payee:** an account number ending in **0** → `NO_MATCH`; ending in **9** → `CLOSE_MATCH` (the bank holds "J Citizen" for "Jane Citizen"); anything else → `MATCH`.

```powershell
$pns = 'https://localhost:46386/admin/behaviour'
Invoke-RestMethod $pns -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / Healthy
Invoke-RestMethod $pns
```

## Configuration

`Simulator:Behaviour` (start-up behaviour), `Simulator:SlowDelaySeconds`, and `Simulator:ApiKey` (Development value in `appsettings.Development.json`; it must match the Payments API's `PaymentNetwork:ApiKey`). The simulator refuses to start without an API key.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | An API key on every business call, compared in constant time; no key configured = no start | Anyone on the network could call the "external" system | Unauthenticated use of a partner API; timing attacks on the key | `Simulator:ApiKey`, `CryptographicOperations.FixedTimeEquals` in [Program.cs](Program.cs) |
| 2 | `Idempotency-Key` required; the same key always returns the same result | A retried request after a timeout would act twice (two accounts, two payments) | Duplicated money movement | `Idempotency-Key` in [Program.cs](Program.cs) |
| 3 | The behaviour switch (`/admin/behaviour`) answers only loopback callers | Anyone could switch the provider to Down | Unauthorised control of a test dependency | `IsLoopback` in [Program.cs](Program.cs) |
| 4 | Host filtering: `localhost` only | Requests addressed to other host names would be served | Host-header attacks | [appsettings.Development.json](appsettings.Development.json) |