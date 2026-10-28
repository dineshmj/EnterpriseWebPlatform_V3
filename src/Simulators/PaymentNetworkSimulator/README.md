# Payment Network Simulator

Stands in for the **external payment network** - an NPP-style clearing service that moves the money to the payee's bank - so the Payments saga can show what happens when a third party is slow, failing, down, refusing or loses its answers: retries, the circuit breaker, **compensation** (releasing the reserved funds) after a refusal, and **reconciliation** (asking whether a payment was sent) when the outcome is unknown. It belongs to no bounded context; it keeps its payments in memory.

URL: `https://localhost:46386` (launch profile `https`). The Payments API's saga step runner calls it **directly over HTTP** (not through Kafka).

## API

| Endpoint | Who | Purpose |
|---|---|---|
| `POST /v1/payments` | Payments API, headers `X-Api-Key` (constant-time comparison) and **`Idempotency-Key`** | Send a payment: `201 { networkReference, status: "SETTLED" }`, or `422 { reason }` |
| `GET /v1/payments/by-idempotency-key/{key}` | Payments API, `X-Api-Key` | Status enquiry: `{ status: "SENT", networkReference }`, `{ status: "REFUSED", reason }` or 404 `{ status: "NOT_FOUND" }` (never received); 503 / 500 while `Down` / `Failing` |
| `GET /v1/bsb/{bsb}` | Payments API, `X-Api-Key` | BSB directory: `{ bsb, bank, branch, state, npp }`; 404 when unknown |
| `POST /v1/payee-confirmations` | Payments API, `X-Api-Key` | Confirmation of Payee: `{ result: MATCH / CLOSE_MATCH / NO_MATCH, accountNameHeld }` |
| `POST /v1/fraud-scores` | Payments API, `X-Api-Key` | The network's fraud score for a payment about to be made: `{ score: 0-999, decision: ALLOW / REVIEW / DECLINE, reasons: [...] }` (demo rules below) |
| `GET /admin/behaviour` | localhost, or `X-Simulator-Admin-Key` | Current behaviour |
| `PUT /admin/behaviour` | localhost, or `X-Simulator-Admin-Key` | Change behaviour at runtime: `{"behaviour":"Down"}` |
| `GET /fraud/admin/behaviour`, `PUT /fraud/admin/behaviour` | localhost, or `X-Simulator-Admin-Key` | The fraud scoring's OWN behaviour: `Healthy`, `Slow` (answers after `FraudSlowDelayMilliseconds`, 2000 - past the Payments API's 250 ms budget), `Failing` (500), `Down` (503). Operations lists it as "Fraud Scoring (Payment Network)" |
| `GET /health` | anyone | Liveness |

## Idempotency

The same `Idempotency-Key` always returns the **same** answer (the payment's network reference, or the same refusal), whatever the behaviour is now: the payment was already sent, only the answer was lost. The Payments API sends the PaymentRef as the key, which is what makes its retries safe - a payment can never be sent twice.

## Behaviours

| Behaviour | Response (new key) | What the saga does |
|---|---|---|
| `Healthy` | 201 | Settles the funds: payment COMPLETED |
| `Slow` | 201 after `SlowDelaySeconds` (12) | The 5-second attempt timeout fires: retried |
| `Failing` / `Down` | 500 / 503 (the status enquiry too) | Retries with back-off; after 4 attempts the outcome is unknown, so it asks the status (CONFIRM_WITH_NETWORK) - again and again while the network is down, funds still reserved. Back to Healthy: "never received" → releases the funds, payment FAILED. Unconfirmed for too long: operations reconcile it with four eyes |
| `LosingAnswers` | 504 for every send - but the payment IS sent (once per key) | After 4 sends without an answer it asks the status: "sent" → settles, payment COMPLETED - paid once, never refunded by mistake |
| `Refusing` | 422 `{ reason }` | A permanent "no": releases the funds at once: payment FAILED |

Whatever the behaviour, a **payee BSB starting with 999** is refused ("the payee's account is closed"), so one payment can show the compensation path without changing anything. A BSB that is not in the directory is refused too.

The same switch is on the Operations **Simulator & Fault Controls** screen (`daniel.ops`, Development and Test only; it goes back to Healthy by itself after 15 minutes).

## BSB directory and Confirmation of Payee (demo rules)

- **BSB directory:** the first two digits name the bank (01 ANZ, 03 / 73 Westpac, 06 Commonwealth Bank, 08 NAB, 11 St.George, 18 Macquarie, 48 Suncorp, 63 Bendigo, 80 Cuscal, 99 "Closed Bank (demo)"), the third the state. Anything else is unknown.
- **Confirmation of Payee:** an account number ending in **0** → `NO_MATCH`; ending in **9** → `CLOSE_MATCH` (the bank holds "J Citizen" for "Jane Citizen"); anything else → `MATCH`.

```powershell
$pns = 'https://localhost:46386/admin/behaviour'
Invoke-RestMethod $pns -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / LosingAnswers / Healthy
Invoke-RestMethod $pns
```

## Fraud scoring (demo rules)

Fixed, so a demo and a test know the answer: a payee account number ending in **13** is on the network's mule list → `DECLINE` (950, `PAYEE_ON_MULE_LIST`); **AUD 50,000** or more → `REVIEW` (720, `HIGH_AMOUNT`); **AUD 500** or more to a payee the customer never paid (`firstPaymentToPayee`, sent by the Payments API) → `REVIEW` (680, `NEW_PAYEE`, `HIGH_AMOUNT_FOR_NEW_PAYEE`); anything else → `ALLOW` (a score below 500). Its behaviour is switched apart from the payments', so the network can send payments while its scoring is down - the Payments API's fallback rules then decide.

```powershell
Invoke-RestMethod 'https://localhost:46386/fraud/admin/behaviour' -Method Put -ContentType 'application/json' -Body '{"behaviour":"Slow"}'   # or Failing / Down / Healthy
```

## Configuration

`Simulator:Behaviour` (start-up behaviour), `Simulator:SlowDelaySeconds`, `Simulator:FraudBehaviour` and `Simulator:FraudSlowDelayMilliseconds` (the fraud scoring's), and `Simulator:ApiKey` (Development value in `appsettings.Development.json`; it must match the Payments API's `PaymentNetwork:ApiKey`). The simulator refuses to start without an API key.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | An API key on every business call, compared in constant time; no key configured = no start | Anyone on the network could call the "external" system | Unauthenticated use of a partner API; timing attacks on the key | `Simulator:ApiKey`, `CryptographicOperations.FixedTimeEquals` in [Program.cs](Program.cs) |
| 2 | `Idempotency-Key` required; the same key always returns the same result | A retried request after a timeout would act twice (two accounts, two payments) | Duplicated money movement | `Idempotency-Key` in [Program.cs](Program.cs) |
| 3 | The behaviour switches (`/admin/behaviour`, and the fraud scoring's `/fraud/admin/behaviour`) answer only loopback callers - or, where the simulator runs on another machine than the Operations Journey API (the cloud demo), a caller sending the configured `Simulator:AdminKey` in `X-Simulator-Admin-Key` (compared in constant time; unset = loopback only) | Anyone could switch the provider to Down | Unauthorised control of a test dependency | `IsAdmin` / `IsLocal` in [Program.cs](Program.cs) |
| 4 | Host filtering: `localhost` only | Requests addressed to other host names would be served | Host-header attacks | [appsettings.Development.json](appsettings.Development.json) |
| 5 | Container image: built only by CI from a commit, chiseled non-root runtime (no shell, files read-only to the process), base images pinned by tag and digest, scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); its API key comes from the environment - none of them is in the image. Development, test and demo environments only - never deployed to production. | A developer-built or tampered image could run in an environment; a credential in a layer stays in the registry for good; a shell in the image helps an attacker who gets in | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../.dockerignore), [container-images.yml](../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../doc/Containers.md) |