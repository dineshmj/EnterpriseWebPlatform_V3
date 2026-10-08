# Accounts BFF + MFE

The account officer's front end: a **Next.js** micro-frontend (static export) served by an **ASP.NET Core 10** BFF (Duende BFF). The Shell embeds it as the **Account Applications**, **Accounts** and **Account Lifecycle** menu items. Business requirements: [Accounts-Requirements.md](../doc/Accounts-Requirements.md).

URL: `https://accounts.dev.localhost:45456` (launch profile `https`).

## Screens

| Path | Purpose |
|---|---|
| `/v1/accounts/applications/view-all` | Work queue of the officer's branch: *Awaiting decision*, *On hold*, *Opening*, *Failed*, *All*. |
| `/v1/accounts/applications/view-details?applicationId=…` | One application: details, the account (product, account number, or the core-banking retry / failure state), the people separation of duties excludes, and the officer's actions: claim, release, **approve (choosing the product)**, reject, put on hold, release hold. While core banking opens the account, the page refreshes itself until it is open. |
| `/v1/accounts/view-all` | The branch's opened accounts (BSB + account number, holder, product, status, core-banking reference). |
| `/v1/accounts/lifecycle/view-all` | Accounts by lifecycle state (ACTIVE / FROZEN / CLOSED). Freeze and close are planned; the page is read-only. |

The screens hint at the rules (e.g. "You approved this application in Compliance"), but **every rule is enforced by the Accounts API**: branch scope, assignment and separation of duties. The API's explanation is shown when it refuses an action.

## BFF

| Endpoint | Forwards to (Accounts API, with the officer's access token) |
|---|---|
| `GET bff/api/accounts/applications?status=` | `GET v1/accounts/applications` |
| `GET bff/api/accounts/applications/{id}` | `GET v1/accounts/applications/{id}` |
| `POST bff/api/accounts/applications/{id}/{claim\|release\|approve\|reject\|hold\|release-hold}` (anti-forgery token required) | the same action (approve carries `product`) |
| `GET bff/api/accounts/accounts` | `GET v1/accounts` |
| `GET api/auth/silent-login`, `api/auth/user`, `api/auth/csrf`, `POST api/auth/logout` | session endpoints (as in the other .NET BFFs) |

- **Tokens stay on the server.** The browser holds only the `__Host-Microservice-Accounts-bff` session cookie (HttpOnly, Secure, SameSite=Lax); sessions are server-side and end on front-channel or back-channel logout.
- **Sessions survive a restart.** Sessions and the Data Protection keys that encrypt the cookies are stored in PostgreSQL (`EwpBffStateDb`, schema `accounts_bff`, used only by the database user `ewp_accounts_bff`). An API call whose session has expired answers **401** (never a redirect); the MFE then signs in again silently and returns to the same page. Only `silent-login` and `user` are anonymous.
- **Rate limited** per person, per machine client and per IP before sign-in; over the limit the answer is 429 with `Retry-After` ([RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs)).
- **Resilience to the API:** per-attempt (10 s) and total (30 s) timeouts and a circuit breaker on every call; **GETs only** are retried, because officer actions are not idempotent. When the API is unreachable or the circuit is open, the BFF answers 503 with a readable message.
- **CSP:** scripts only from the BFF plus the SHA-256 hashes of the exported pages' inline scripts, computed at start-up; framed only by the Shell and the IDP. Restart the BFF after re-exporting the MFE.

## Build and run

```powershell
cd client-app
pnpm install
pnpm run export          # next build + copy of the export into ..\wwwroot
```

`ps\build\CompileAndExportBFFClients_V3.ps1` does this for every front end (step 6). The BFF itself is part of the solution's launch profile. Its client secret is `Oidc:ClientSecret` (Development value in `appsettings.Development.json`; elsewhere `Oidc__ClientSecret` from the environment or a secret store).