# Core Banking Simulator

Stands in for the bank's **external core-banking system** — the system of record that actually opens accounts and issues the BSB and account number — so the Accounts context can show how the platform survives a third party that is slow, failing, down or refusing, and how it retries a POST **without opening two accounts**. It belongs to no bounded context; it keeps its accounts in memory.

URL: `https://localhost:46376` (launch profile `https`).

## API

| Endpoint | Who | Purpose |
|---|---|---|
| `POST /v1/accounts` | Accounts API, headers `X-Api-Key` (compared in constant time) and **`Idempotency-Key`** | Open an account: returns `accountNumber`, `bsb` (`062-000`), `reference`, `product` |
| `GET /admin/behaviour` | localhost only | Current behaviour |
| `PUT /admin/behaviour` | localhost only | Change behaviour at runtime: `{"behaviour":"Down"}` |
| `GET /health` | anyone | Liveness |

## Idempotency

The same `Idempotency-Key` always returns the **same** account (200), even while the simulator is failing: the opening already happened, only the answer was lost. The Accounts API sends the onboarding's `ApplicationRef` as the key, which is what makes its retries safe.

## Behaviours

| Behaviour | Response (new key) | What the Accounts API does |
|---|---|---|
| `Healthy` | 201 with the account | Records it: application OPENED, account ACTIVE, `AccountOpened` → CO COMPLETED |
| `Slow` | 201 after `SlowDelaySeconds` (12) | The 5-second attempt timeout fires; a technical failure, retried |
| `Failing` | 500 | Retries with back-off; after 6 failures the application is FAILED |
| `Down` | 503 | As Failing |
| `Refusing` | 422 `{ reason }` | A permanent "no": the application is FAILED at once (no retries) |

While failing or down, approved applications stay **OPENING** (never "opened"), the circuit breaker stops hammering the system, and the Accounts API's `/health/ready` turns Degraded after 2 minutes. Back to `Healthy`, the waiting accounts are opened automatically.

```powershell
$cbs = 'https://localhost:46376/admin/behaviour'
Invoke-RestMethod $cbs -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / Healthy
Invoke-RestMethod $cbs
```

## Configuration

`Simulator:Behaviour` (start-up behaviour), `Simulator:SlowDelaySeconds`, `Simulator:Bsb`, and `Simulator:ApiKey` (Development value in `appsettings.Development.json`; it must match the Accounts API's `CoreBanking:ApiKey`). The simulator refuses to start without an API key.