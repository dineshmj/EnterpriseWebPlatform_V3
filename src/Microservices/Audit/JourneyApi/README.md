# Audit Journey API (NestJS)

The **Journey API** tier of the customer's pattern - an Experience / Edge API, deployed on its own between the [Audit web](../BFF.Web/README.md) app (Next.js light BFF) and the Domain APIs:

```text
Audit SPA ──ajax──► Next.js server action (light BFF)
              ──token exchange──► THIS Journey API
                         ──token exchange──► Audit API     (the trail)
                         ──token exchange──► Payments API  (a payment's current status)
```

It holds no data and no business rules. It turns one screen's request into the Domain API calls that screen needs - **always acting for the signed-in person**, never as itself.

URL: `https://audit-journey.dev.localhost:46379`. Runs outside Visual Studio: `runnow.bat`.

## How the person travels: token exchange (RFC 8693)

Every hop authenticates as its own client **and** carries the person. At each hop the caller asks the IDP to exchange the token it holds for a new, short-lived one aimed at the next service:

| Token | Audience | `sub` | `act` (who is acting for the person) |
|---|---|---|---|
| Sarah signs in to the Audit web BFF | (sign-in) | Sarah | - |
| The BFF exchanges it for this API | `audit-journey-api` | Sarah | `{ client_id: Audit.Microservice.Web.ClientID }` |
| This API exchanges it for the Audit API | `audit-api` | Sarah | `{ client_id: Audit.JourneyApi.ClientID, act: { client_id: Audit.Microservice.Web.ClientID } }` |
| ... and, separately, for the Payments API | `payments-api` | Sarah | the same chain |

So every Domain API decides by **Sarah's** permissions (the IDP re-reads them at each exchange) and knows exactly which services are calling. One token per downstream API: a token meant for the Audit API can never be replayed at Payments. The IDP's allow-list (`TokenExchangeGrantValidator`) fixes who may exchange what:

- the web BFF: only a person's token it obtained itself at sign-in;
- this API: only a token the web BFF exchanged for it. Its **only** grant is token exchange - it has no client-credentials grant, so it can never call a Domain API without a person behind it.

A deactivated person's token stops working along the whole chain (the IDP checks the user at every exchange).

## Endpoints

All require a delegated token: audience `audit-journey-api`, scope `audit-journey.read`, `act` = the Audit web BFF, and the person's permission.

| Endpoint | Permission | Calls |
|---|---|---|
| `GET /v1/journeys/audit/entries?record=&person=&eventType=&from=&to=&kind=&pageNumber=&pageSize=` | `audit.search` | Audit API search |
| `GET /v1/journeys/audit/records/{recordRef}` | `audit.view` | Audit API timeline **and**, for `PAY-…`, Payments API `GET /v1/payments/by-number/{number}`, in parallel. If Payments is down, the timeline still comes back, with the reason the current status is missing. |
| `GET /v1/journeys/audit/integrity` | `audit.view` | Audit API chain verification |
| `GET /health/live`, `/health/ready` | - | - |

Downstream answers are mapped, never passed through: 401/403 → 403, 404 → 404, unavailable → 503. GETs are retried once.

## Run it

1. Once: export the ASP.NET Core development certificate (it covers `*.dev.localhost`) to your user profile:
   ```powershell
   dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"
   dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password
   ```
2. Hosts file: `127.0.0.1    audit-journey.dev.localhost`.
3. `runnow.bat` (installs, builds and starts; the development client secret is set there - elsewhere supply `AUDIT_JOURNEY_CLIENT_SECRET` from a secret store). Every setting is an environment variable: `src/configuration/journey-options.ts`. A missing one stops the start-up. `.\ps\run\Start-NodeServices.ps1` in the repository root starts it with the other Node services.
4. Debugging: `runnow.bat dev` in VS Code's **JavaScript Debug Terminal** runs it in watch mode with source maps - breakpoints in the TypeScript sources are hit, and a saved change restarts it.

## Try the refusals

The happy path is the Audit Trail screen (sign in as `sarah.audit`). The refusals can be checked directly (PowerShell, IDP running):

```powershell
# From the repository root. Show-Refusal prints the status and the body of a refused request
# (Windows PowerShell 5.1 hides it otherwise).
function Show-Refusal($label, [scriptblock] $call) {
  try { & $call | Out-Null; "$label -> SUCCEEDED (unexpected!)" }
  catch {
    $r = $_.Exception.Response
    if ($r) { "$label -> HTTP {0}: {1}" -f [int]$r.StatusCode, (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
    else { "$label -> " + $_.Exception.Message }
  }
}

$idp = 'https://idp.dev.localhost:46392/connect/token'
$secrets = (Get-Content .\src\IDP\appsettings.Development.json -Raw | ConvertFrom-Json).ClientSecrets
$journey = @{ client_id='Audit.JourneyApi.ClientID'; client_secret=$secrets.'Audit.JourneyApi.ClientID' }

Show-Refusal '1 client credentials' { Invoke-RestMethod $idp -Method Post -Body ($journey + @{ grant_type='client_credentials' }) }

$machine = (Invoke-RestMethod $idp -Method Post -Body @{ grant_type='client_credentials'; client_id='Notifications.Subscriber.To.NotificationsApi.M2M.ClientID'; client_secret=$secrets.'Notifications.Subscriber.To.NotificationsApi.M2M.ClientID'; scope='notifications.write' }).access_token
"machine token obtained: " + [bool]$machine

Show-Refusal '2 exchange machine token' { Invoke-RestMethod $idp -Method Post -Body ($journey + @{ grant_type='urn:ietf:params:oauth:grant-type:token-exchange'; subject_token=$machine; subject_token_type='urn:ietf:params:oauth:token-type:access_token'; scope='audit.read' }) }
Show-Refusal '3 Audit API' { Invoke-WebRequest https://audit-api.dev.localhost:46378/v1/audit/integrity -UseBasicParsing -Headers @{ Authorization = "Bearer $machine" } }
Show-Refusal '4 Journey API' { Invoke-WebRequest https://audit-journey.dev.localhost:46379/v1/journeys/audit/integrity -UseBasicParsing -Headers @{ Authorization = "Bearer $machine" } }
```

Expected - every line a refusal:

```text
1 client credentials -> HTTP 400: {"error":"unauthorized_client"}
machine token obtained: True
2 exchange machine token -> HTTP 400: {"error":"invalid_grant","error_description":"The subject token has no person (sub)."}
3 Audit API -> HTTP 401:
4 Journey API -> HTTP 401:
```

`invalid_client` on 1 or 2 means the IDP running is older than this step; "Unable to connect" means that service is not running; any `SUCCEEDED` is a real problem.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Only delegated tokens: right issuer and audience, `typ: at+jwt`, scope `audit-journey.read`, a person as `sub`, and `act` = the Audit web app | Any token, or a machine token without a person, would be accepted | Broken authentication; calls without a person behind them | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 2 | The person's permission (`audit.search` / `audit.view`) checked per endpoint | Any signed-in person could read the audit trail | Broken function-level authorization (OWASP API5) | `RequiresPermission` in [audit-journeys.controller.ts](src/controllers/audit-journeys.controller.ts) |
| 3 | Exchange-only client: no client-credentials grant; one exchanged token per downstream API | The Journey API could call Domain APIs as itself; a token for one API could be replayed at another | Privilege escalation without a person; cross-API token replay | [AuditJourneyApiTokenExchange.cs](../../../IDP/ConfigRegistration/Clients/M2M/AuditJourneyApiTokenExchange.cs), [token-exchange.service.ts](src/services/token-exchange.service.ts) |
| 4 | Downstream answers mapped, never passed through (401/403 → 403, unavailable → 503) | Internal error details of the Domain APIs would reach the browser | Information disclosure | [domain-api.client.ts](src/services/domain-api.client.ts) |
| 5 | `nosniff`; no data stored | Responses could be sniffed as another type; a breach would expose stored data | MIME sniffing; data exposure | [main.ts](src/main.ts) |
| 6 | Request limits: overall (600 a minute, before any token check) and 120 a minute per person and calling client; 429 with `Retry-After`; per instance (in memory; deployment-wide limits need a gateway or shared store, Threat-Model R-14), expired windows swept every minute | A stolen session or a runaway client could flood the API and the Domain APIs behind it | Unrestricted resource consumption (OWASP API4) | [rate-limit.ts](src/http/rate-limit.ts), [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 7 | Errors as Problem Details (RFC 9457); unexpected errors and the IDP's failure reasons stay in the log | Internal messages would reach the caller | Information disclosure (CWE-209) | [problem-details.filter.ts](src/http/problem-details.filter.ts), [token-exchange.service.ts](src/services/token-exchange.service.ts) |
| 8 | No default for any security setting: audience, permitted caller and own client must be configured | A copied default could accept tokens meant for another service | Misconfiguration (OWASP A05) | [journey-options.ts](src/configuration/journey-options.ts) |
| 9 | Container image: CI builds the API from the committed lockfile (`--frozen-lockfile`) - never a local build; production dependencies only; slim Node runtime without npm, npx, corepack or yarn, as the non-root user 1000 with its files read-only to it; scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); no secret inside - every setting comes from the environment, and `runnow.bat` and key material never enter the build | A developer-built image could carry anything; a package manager in a container helps an attacker who gets in; a secret in a layer stays in the registry for good | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 10 | TLS here or at the platform, never neither by accident: `AUDIT_JOURNEY_PLAIN_HTTP=true` (the image sets it) listens on plain HTTP 8080 - TLS between pods comes from the platform (service mesh mTLS); otherwise the development certificate (`AUDIT_JOURNEY_TLS_PFX_PATH`) is required, and both set refuse to start | A missing setting could quietly open plain HTTP | Misconfiguration (OWASP A05); cleartext transmission (CWE-319) | [listener-tls.ts](src/configuration/listener-tls.ts) |

## Not yet

- Structured logs, metrics and traces in this Node tier (the .NET services have them).