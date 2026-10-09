# Payments BFF + MFE

The Payments micro-frontend (Next.js static export in `wwwroot`) and its Backend-for-Frontend (ASP.NET Core 10, Duende BFF). The browser holds a session cookie only; the staff member's tokens stay in this BFF's server-side session. Framed by the Shell; may be framed by nothing else (CSP `frame-ancestors`).

URL: `https://payments.dev.localhost:46388`. IDP client: `Payments.Microservice.BFF.ClientID` (scopes `payments.read`, `payments.write` and, read-only, `accounts.read`).

## Pages

| Page | Who | What |
|---|---|---|
| `/v1/payments/new` | Customer service agent (`payment.initiate`) | The assisted-channel payment: (1) find the customer and choose the paying account, with its **available** amount; (2) payee BSB (looked up in the BSB directory), account number and name, with **Confirmation of Payee**; (3) amount and reference; (4) review and **Transfer**. |
| `/v1/payments/view-details?paymentId=` | Staff of the branch | Status, outcome, the saga's state, and the **timeline** - re-read every 2 seconds while the payment is in progress. |
| `/v1/payments/view-all` | Staff of the branch | The branch's payments with status filters. |
| `/v1/payments/processing/view-all` | Operations (all branches), payments officers (own branch) | The **Payment Processing Monitor**: counts and every unfinished saga, most urgent first, refreshed every 5 s. Operations open a COMPENSATION_FAILED payment and press **Retry release**. |
| `/v1/payments/approvals/view-all` | Payments officer (`payment.approve`) | The approval queue: the branch's payments above the tier. The status page then shows **Your decision**: Approve and send, or Reject (remarks required). It explains up front when the officer started the payment (SoD) or the amount is above their clearance's limit; the API enforces both. |

The New payment form creates ONE Idempotency-Key when it opens: pressing Transfer twice, or trying again after an error, can never create a second payment. Typed details are protected by the Shell's unsaved-changes check. A Confirmation of Payee "close match" offers the name the bank holds; a "no match" needs an explicit confirmation that the details were checked with the customer (a common scam signal).

The screen explains limits early (available funds, the approval tier from `GET /v1/payments/policy`), but every rule is decided by the APIs: the Payments API validates the payment, and Accounts reserves the funds under the account's row lock.

## API facade (`/bff/api`, anti-forgery header on every POST)

| Route | Forwards to |
|---|---|
| `GET payments`, `GET payments/{id}` | Payments API `GET /v1/payments`, `/v1/payments/{id}` |
| `POST payments` (header `Idempotency-Key`) | Payments API `POST /v1/payments` - passed through unchanged |
| `GET payments/policy`, `GET payments/bsb/{bsb}`, `POST payments/payee-confirmations` | Payments API (the last two are answered by the payment network) |
| `POST payments/{id}/approve`, `POST payments/{id}/reject` | Payments API (the officer's decision) |
| `GET payments/processing`, `POST payments/{id}/retry-release` | Payments API (monitor; operations recovery) |
| `GET payer-accounts?search=` | Accounts API `GET /v1/accounts/for-payment` - the branch's ACTIVE accounts with their available amount (policy: `accounts.read` + `payment.initiate` + branch) |

Every call carries the staff member's own access token; GETs are retried, POSTs never (the screen resends with the same Idempotency-Key instead). `silent-login` accepts only the five pages above (the details page with exactly one numeric `paymentId`).

- **Sessions survive a restart.** Sessions and the Data Protection keys that encrypt the cookies are stored in PostgreSQL (`EwpBffStateDb`, schema `payments_bff`, used only by the database user `ewp_payments_bff`). An API call whose session has expired answers **401** (never a redirect); the MFE then signs in again silently and returns to the same page. Only `silent-login` and `user` are anonymous.
- **Rate limited** per person, per machine client and per IP before sign-in; over the limit the answer is 429 with `Retry-After` ([RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs)).

## Build

`ps\build\CompileAndExportBFFClients_V3.ps1` (step 7) builds the MFE and copies it to `wwwroot`; restart this BFF afterwards (its CSP hashes are computed at start-up).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens stay on the server (BFF pattern): the browser holds only an HttpOnly session cookie; Duende attaches the user's token to API calls | Any XSS bug could read the access and refresh tokens from JavaScript | Token theft (OWASP A07) | `AddBff`, `AddUserAccessTokenHandler` in [Program.cs](Program.cs); [api.ts](client-app/app/lib/api.ts) sends no `Authorization` header |
| 2 | Authorization code + PKCE as a confidential client; the client secret comes from configuration and the BFF refuses to start without it | An intercepted authorization code could be redeemed; a default secret could ship to production | Code interception / injection (RFC 9700) and leaked defaults | `AddOpenIdConnect` (`UsePkce`, `Oidc:ClientSecret`) in [Program.cs](Program.cs) |
| 3 | Session cookie `__Host-`, HttpOnly, Secure, `SameSite=Lax`, 30 minutes sliding | The cookie could be read by script, sent over HTTP, set by a sibling sub-domain, or ride along on cross-site requests | Session theft, session fixation and CSRF | `AddCookie` in [Program.cs](Program.cs) |
| 4 | Anti-forgery token in a header on every state-changing request | A hostile page could make the signed-in officer's browser post a decision | Cross-site request forgery (OWASP A01) | `AddAntiforgery` in [Program.cs](Program.cs); `[ValidateAntiForgeryToken]` in [PaymentsApiController.cs](Controllers/PaymentsApiController.cs), [AuthController.cs](Controllers/AuthController.cs) |
| 5 | Server-side sessions and the cookie-encryption key ring in PostgreSQL (`EwpBffStateDb`, schema `payments_bff`, its own user); the key ring encrypted at rest (certificate, or DPAPI in Development), fail closed | A restart would sign everybody out; a stolen key ring could forge cookies; a session could not be ended from the server | Session forgery and irrevocable sessions | [PersistentDataProtection.cs](../../../Common/WebUtilities/Security/PersistentDataProtection.cs), [EwpBffStateDb.sql](../../../../db/EwpBffStateDb.sql) |
| 6 | Strict Content-Security-Policy: scripts and styles only from this BFF plus the exported inline blocks by SHA-256 hash; no `'unsafe-inline'`; `object-src 'none'`; `form-action 'self'` | An injected script or style would run (steal data, fake the screen) | Cross-site scripting and CSS injection (OWASP A03) | [ContentSecurityPolicy.cs](../../../Common/WebUtilities/Security/ContentSecurityPolicy.cs), `ContentSecurityPolicy.Build` in [Program.cs](Program.cs) |
| 7 | Framing allowed only for the Shell and the IDP (`frame-ancestors`) | Any site could frame the screens and trick a click | Clickjacking | `frameAncestors` in [Program.cs](Program.cs) |
| 8 | `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` | The browser could guess a response as script; full URLs would leak to other sites | MIME sniffing; information leakage via the Referer header | Security-header middleware in [Program.cs](Program.cs) |
| 9 | Sign-in returns only to allow-listed routes | A crafted link could bounce a signed-in user to a phishing site | Open redirect (CWE-601) | [BffRouteCatalog.cs](Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](Controllers/AuthController.cs) |
| 10 | Shell–MFE messages accepted only from the configured Shell origin and the parent window | A hostile page that frames the MFE could pose as the Shell and steer it | Cross-origin message spoofing | [MfeShell.tsx](client-app/app/components/MfeShell.tsx) |
| 11 | Single sign-out: front-channel (`/signout-oidc` clears this session) and back-channel logout | A session would survive signing out in the Shell | Session reuse after logout | `SignOutScheme`, `MapBffManagementEndpoints` in [Program.cs](Program.cs) |
| 12 | Calls to the API: timeouts, circuit breaker, retries for GET only | A hung API would pile up requests; a retried POST could act twice | Resource exhaustion and duplicated writes | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 13 | Per-caller rate limits, host filtering (`AllowedHosts`), HSTS | One user could flood the BFF; a forged `Host` header could poison links; HTTP downgrade | Resource exhaustion (OWASP API4), host-header attacks, interception | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs); [appsettings.Development.json](appsettings.Development.json) |
| 14 | No inline styles in the front end; its own not-found page (Next.js's default one carries inline CSS) | The CSP would need `'unsafe-inline'` for styles | CSS injection | [not-found.tsx](client-app/app/not-found.tsx) |