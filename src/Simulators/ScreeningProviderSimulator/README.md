# Screening Provider Simulator

Stands in for an **external AML / sanctions / PEP screening vendor**, so the Compliance context can show how the platform survives a third party that is slow, failing or down. It is not part of any bounded context and holds no data.

URL: `https://localhost:46366` (launch profile `https`).

## API

| Endpoint | Who | Purpose |
|---|---|---|
| `POST /v1/screenings` | Compliance API, header `X-Api-Key` (compared in constant time) | Screen a customer; returns the outcome and a provider reference |
| `GET /admin/behaviour` | localhost only | Current behaviour |
| `PUT /admin/behaviour` | localhost only | Change behaviour at runtime: `{"behaviour":"Down","forcedOutcome":null}` |
| `GET /health` | anyone | Liveness |

## Behaviours

| Behaviour | Response | What the Compliance API does |
|---|---|---|
| `Healthy` | 200 at once | Records the result; the case moves to UNDER_REVIEW |
| `Slow` | 200 after `SlowDelaySeconds` (12) | The 5-second attempt timeout fires; treated as a failure |
| `Failing` | 500 | Retries, then records a failure; circuit opens after repeated failures |
| `Down` | 503 | As Failing |

On any failure the case **stays in SCREENING** and is retried later with back-off. A provider failure is never a pass.

## Outcome

Deterministic from the customer number's last digit, so a demo can pick the risk: `9` → `MATCH` (HIGH risk), `7` or `8` → `POTENTIAL_MATCH` (MEDIUM), otherwise `CLEAR` (LOW). `forcedOutcome` (`CLEAR`, `POTENTIAL_MATCH`, `MATCH`) overrides it until set back to `null`.

```powershell
$sim = 'https://localhost:46366/admin/behaviour'
Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'
Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Healthy","forcedOutcome":"MATCH"}'
Invoke-RestMethod $sim
```

## Configuration

`Simulator:Behaviour` (start-up behaviour), `Simulator:SlowDelaySeconds`, and `Simulator:ApiKey` (Development value in `appsettings.Development.json`; it must match the Compliance API's `ScreeningProvider:ApiKey`). The simulator refuses to start without an API key.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | An API key on every business call, compared in constant time; no key configured = no start | Anyone on the network could call the "external" system | Unauthenticated use of a partner API; timing attacks on the key | `Simulator:ApiKey`, `CryptographicOperations.FixedTimeEquals` in [Program.cs](Program.cs) |
| 2 | The behaviour switch (`/admin/behaviour`) answers only loopback callers | Anyone could switch the provider to Down | Unauthorised control of a test dependency | `IsLoopback` in [Program.cs](Program.cs) |
| 3 | Host filtering: `localhost` only | Requests addressed to other host names would be served | Host-header attacks | [appsettings.Development.json](appsettings.Development.json) |