# Compliance BFF + MFE

The Compliance officer's front end: a **Next.js** micro-frontend (static export) served by an **ASP.NET Core 10** BFF (Duende BFF). The Shell embeds it as the **Compliance Monitor** menu item. Business requirements: [Compliance-Requirements.md](../doc/Compliance-Requirements.md).

URL: `https://compliance.dev.localhost:44399` (launch profile `https`).

## Screens

| Path | Purpose |
|---|---|
| `/v1/compliance/view-all` | Work queue of the officer's branch: *Awaiting decision*, *On hold*, *Screening*, *All*. Risk rating and the clearance an approval needs are shown per case. |
| `/v1/compliance/cases/view-details?caseId=…` | One case: details, screening result (or the provider retry state), the people separation of duties excludes, and the officer's actions: claim, release, approve, reject, put on hold, release hold. |

The screens hint at the rules (e.g. "this HIGH case needs clearance 5", "you decided this customer's KYC"), but **every rule is enforced by the Compliance API**: branch scope, assignment, separation of duties and clearance by risk. The API's explanation is shown when it refuses an action.

## BFF

| Endpoint | Forwards to (Compliance API, with the officer's access token) |
|---|---|
| `GET bff/api/compliance/cases?status=` | `GET v1/compliance/cases` |
| `GET bff/api/compliance/cases/{id}` | `GET v1/compliance/cases/{id}` |
| `POST bff/api/compliance/cases/{id}/{claim\|release\|approve\|reject\|hold\|release-hold}` (anti-forgery token required) | the same action |
| `GET api/auth/silent-login`, `api/auth/user`, `api/auth/csrf`, `POST api/auth/logout` | session endpoints (as in the Customer Onboarding BFF) |

- **Tokens stay on the server.** The browser holds only the `__Host-Microservice-Compliance-bff` session cookie (HttpOnly, Secure, SameSite=Lax); sessions are server-side and end on front-channel or back-channel logout.
- **Resilience to the API:** per-attempt (10 s) and total (30 s) timeouts and a circuit breaker on every call; **GETs only** are retried, because officer actions are not idempotent. When the API is unreachable or the circuit is open, the BFF answers 503 with a readable message.
- **CSP:** scripts only from the BFF plus the SHA-256 hashes of the exported pages' inline scripts, computed at start-up; framed only by the Shell and the IDP. Restart the BFF after re-exporting the MFE.

## Build and run

```powershell
cd client-app
pnpm install
pnpm run export          # next build + copy of the export into ..\wwwroot
```

`CompileAndExportBFFClients_V3.ps1` (repository root) does this for every front end. The BFF itself is part of the solution's launch profile. Its client secret is `Oidc:ClientSecret` (Development value in `appsettings.Development.json`; elsewhere `Oidc__ClientSecret` from the environment or a secret store).