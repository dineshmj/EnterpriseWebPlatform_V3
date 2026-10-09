# Compliance BFF + MFE

The Compliance officer's front end: a **Next.js** micro-frontend (static export) served by an **ASP.NET Core 10** BFF (Duende BFF). The Shell embeds it as the **Compliance Monitor** menu item. Business requirements: [Compliance-Requirements.md](../doc/Compliance-Requirements.md).

URL: `https://compliance.dev.localhost:46399` (launch profile `https`).

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
- **Sessions survive a restart.** Sessions and the Data Protection keys that encrypt the cookies are stored in PostgreSQL (`EwpBffStateDb`, schema `compliance_bff`, used only by the database user `ewp_compliance_bff`). An API call whose session has expired answers **401** (never a redirect); the MFE then signs in again silently and returns to the same page. Only `silent-login` and `user` are anonymous.
- **Rate limited** per person, per machine client and per IP before sign-in; over the limit the answer is 429 with `Retry-After` ([RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs)).
- **Resilience to the API:** per-attempt (10 s) and total (30 s) timeouts and a circuit breaker on every call; **GETs only** are retried, because officer actions are not idempotent. When the API is unreachable or the circuit is open, the BFF answers 503 with a readable message.
- **CSP:** scripts only from the BFF plus the SHA-256 hashes of the exported pages' inline scripts, computed at start-up; framed only by the Shell and the IDP. Restart the BFF after re-exporting the MFE.

## Build and run

```powershell
cd client-app
pnpm install
pnpm run export          # next build + copy of the export into ..\wwwroot
```

`ps\build\CompileAndExportBFFClients_V3.ps1` does this for every front end. The BFF itself is part of the solution's launch profile. Its client secret is `Oidc:ClientSecret` (Development value in `appsettings.Development.json`; elsewhere `Oidc__ClientSecret` from the environment or a secret store).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens stay on the server (BFF pattern): the browser holds only an HttpOnly session cookie; Duende attaches the user's token to API calls | Any XSS bug could read the access and refresh tokens from JavaScript | Token theft (OWASP A07) | `AddBff`, `AddUserAccessTokenHandler` in [Program.cs](Program.cs); [api.ts](client-app/app/lib/api.ts) sends no `Authorization` header |
| 2 | Authorization code + PKCE as a confidential client; the client secret comes from configuration and the BFF refuses to start without it | An intercepted authorization code could be redeemed; a default secret could ship to production | Code interception / injection (RFC 9700) and leaked defaults | `AddOpenIdConnect` (`UsePkce`, `Oidc:ClientSecret`) in [Program.cs](Program.cs) |
| 3 | Session cookie `__Host-`, HttpOnly, Secure, `SameSite=Lax`, 30 minutes sliding | The cookie could be read by script, sent over HTTP, set by a sibling sub-domain, or ride along on cross-site requests | Session theft, session fixation and CSRF | `AddCookie` in [Program.cs](Program.cs) |
| 4 | Anti-forgery token in a header on every state-changing request | A hostile page could make the signed-in officer's browser post a decision | Cross-site request forgery (OWASP A01) | `AddAntiforgery` in [Program.cs](Program.cs); `[ValidateAntiForgeryToken]` in [ComplianceCasesController.cs](Controllers/ComplianceCasesController.cs), [AuthController.cs](Controllers/AuthController.cs) |
| 5 | Server-side sessions and the cookie-encryption key ring in PostgreSQL (`EwpBffStateDb`, schema `compliance_bff`, its own user); the key ring encrypted at rest (certificate, or DPAPI in Development), fail closed | A restart would sign everybody out; a stolen key ring could forge cookies; a session could not be ended from the server | Session forgery and irrevocable sessions | [PersistentDataProtection.cs](../../../Common/WebUtilities/Security/PersistentDataProtection.cs), [EwpBffStateDb.sql](../../../../db/EwpBffStateDb.sql) |
| 6 | Strict Content-Security-Policy: scripts and styles only from this BFF plus the exported inline blocks by SHA-256 hash; no `'unsafe-inline'`; `object-src 'none'`; `form-action 'self'` | An injected script or style would run (steal data, fake the screen) | Cross-site scripting and CSS injection (OWASP A03) | [ContentSecurityPolicy.cs](../../../Common/WebUtilities/Security/ContentSecurityPolicy.cs), `ContentSecurityPolicy.Build` in [Program.cs](Program.cs) |
| 7 | Framing allowed only for the Shell and the IDP (`frame-ancestors`) | Any site could frame the screens and trick a click | Clickjacking | `frameAncestors` in [Program.cs](Program.cs) |
| 8 | `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` | The browser could guess a response as script; full URLs would leak to other sites | MIME sniffing; information leakage via the Referer header | Security-header middleware in [Program.cs](Program.cs) |
| 9 | Sign-in returns only to allow-listed routes | A crafted link could bounce a signed-in user to a phishing site | Open redirect (CWE-601) | [BffRouteCatalog.cs](Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](Controllers/AuthController.cs) |
| 10 | Shell–MFE messages accepted only from the configured Shell origin and the parent window | A hostile page that frames the MFE could pose as the Shell and steer it | Cross-origin message spoofing | [MfeShell.tsx](client-app/app/components/MfeShell.tsx) |
| 11 | Single sign-out: front-channel (`/signout-oidc` clears this session) and back-channel logout | A session would survive signing out in the Shell | Session reuse after logout | `SignOutScheme`, `MapBffManagementEndpoints` in [Program.cs](Program.cs) |
| 12 | Calls to the API: timeouts, circuit breaker, retries for GET only | A hung API would pile up requests; a retried POST could act twice | Resource exhaustion and duplicated writes | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 13 | Per-caller rate limits, host filtering (`AllowedHosts`), HSTS | One user could flood the BFF; a forged `Host` header could poison links; HTTP downgrade | Resource exhaustion (OWASP API4), host-header attacks, interception | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs); [appsettings.Development.json](appsettings.Development.json) |
| 14 | No inline styles in the front end; its own not-found page (Next.js's default one carries inline CSS) | The CSP would need `'unsafe-inline'` for styles | CSS injection | [not-found.tsx](client-app/app/not-found.tsx) |