# Compliance BFF + MFE

The Compliance officer's front end: a **Next.js** micro-frontend (static export) served by an **ASP.NET Core 10** BFF (Duende BFF). The Shell embeds it as the **Compliance Monitor** menu item. Business requirements: [Compliance-Requirements.md](../../../../doc/Requirements/Compliance-Requirements.md).

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

`ps\build\CompileAndExportBFFClients_V3.ps1` does this for every front end. The BFF itself is part of the solution's launch profile. It signs in with its own key (`private_key_jwt`): `Oidc:ClientKey:Path` and `Oidc:ClientKey:PasswordPath` name the encrypted PEM file and its password file (Development: `%USERPROFILE%\.ewp-pki`, made by `ps\pki\New-EwpLocalPki.ps1`; elsewhere both files are mounted from a secret store).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens stay on the server (BFF pattern): the browser holds only an HttpOnly session cookie; Duende attaches the user's token to API calls | Any XSS bug could read the access and refresh tokens from JavaScript | Token theft (OWASP A07) | `AddBff`, `AddUserAccessTokenHandler` in [Program.cs](Program.cs); [api.ts](client-app/app/lib/api.ts) sends no `Authorization` header |
| 2 | Authorization code + PKCE as a confidential client; it proves itself with its own key (`private_key_jwt`, see the last row), configured outside the code - the BFF refuses to start without it | An intercepted authorization code could be redeemed; a default credential could ship to production | Code interception / injection (RFC 9700) and leaked defaults | `AddOpenIdConnect` (`UsePkce`, `Oidc:ClientKey`) in [Program.cs](Program.cs) |
| 3 | Session cookie `__Host-`, HttpOnly, Secure, `SameSite=Lax`, 30 minutes sliding | The cookie could be read by script, sent over HTTP, set by a sibling sub-domain, or ride along on cross-site requests | Session theft, session fixation and CSRF | `AddCookie` in [Program.cs](Program.cs) |
| 4 | Anti-forgery token in a header on every state-changing request; its cookie is `__Host-`, HttpOnly, Secure, `SameSite=Lax` (the screen gets the token from `api/auth/csrf`, never from the cookie) | A hostile page could make the signed-in officer's browser post a decision | Cross-site request forgery (OWASP A01) | `AddAntiforgery` in [Program.cs](Program.cs); `[ValidateAntiForgeryToken]` in [ComplianceCasesController.cs](Controllers/ComplianceCasesController.cs), [AuthController.cs](Controllers/AuthController.cs) |
| 5 | Server-side sessions and the cookie-encryption key ring in PostgreSQL (`EwpBffStateDb`, schema `compliance_bff`, its own user); the key ring encrypted at rest (certificate, or DPAPI in Development), fail closed | A restart would sign everybody out; a stolen key ring could forge cookies; a session could not be ended from the server | Session forgery and irrevocable sessions | [PersistentDataProtection.cs](../../../Common/WebUtilities/Security/PersistentDataProtection.cs), [EwpBffStateDb.sql](../../../../db/EwpBffStateDb.sql) |
| 6 | Strict Content-Security-Policy: scripts and styles only from this BFF plus the exported inline blocks by SHA-256 hash; no `'unsafe-inline'`; `object-src 'none'`; `form-action 'self'` | An injected script or style would run (steal data, fake the screen) | Cross-site scripting and CSS injection (OWASP A03) | [ContentSecurityPolicy.cs](../../../Common/WebUtilities/Security/ContentSecurityPolicy.cs), `ContentSecurityPolicy.Build` in [Program.cs](Program.cs) |
| 7 | Framing allowed only for the Shell and the IDP (`frame-ancestors`) | Any site could frame the screens and trick a click | Clickjacking | `frameAncestors` in [Program.cs](Program.cs) |
| 8 | `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` | The browser could guess a response as script; full URLs would leak to other sites | MIME sniffing; information leakage via the Referer header | Security-header middleware in [Program.cs](Program.cs) |
| 9 | Sign-in returns only to allow-listed routes | A crafted link could bounce a signed-in user to a phishing site | Open redirect (CWE-601) | [BffRouteCatalog.cs](Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](Controllers/AuthController.cs) |
| 10 | Shell–MFE messages accepted only from the configured Shell origin and the parent window - the Shell's origin is this BFF's own setting, read at run time (`/ewp-runtime-config.js`, [shell-origin.ts](client-app/app/lib/shell-origin.ts)) - never built into the front end; missing or not https = no message is sent or accepted | A hostile page that frames the MFE could pose as the Shell and steer it | Cross-origin message spoofing | [MfeShell.tsx](client-app/app/components/MfeShell.tsx) |
| 11 | Single sign-out: front-channel (`/signout-oidc` clears this session) and back-channel logout | A session would survive signing out in the Shell | Session reuse after logout | `SignOutScheme`, `MapBffManagementEndpoints` in [Program.cs](Program.cs) |
| 12 | Calls to the API: timeouts, circuit breaker, retries for GET only | A hung API would pile up requests; a retried POST could act twice | Resource exhaustion and duplicated writes | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 13 | Per-caller rate limits, host filtering (`AllowedHosts`), HSTS | One user could flood the BFF; a forged `Host` header could poison links; HTTP downgrade | Resource exhaustion (OWASP API4), host-header attacks, interception | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs); [appsettings.Development.json](appsettings.Development.json) |
| 14 | No inline styles in the front end; its own not-found page (Next.js's default one carries inline CSS) | The CSP would need `'unsafe-inline'` for styles | CSS injection | [not-found.tsx](client-app/app/not-found.tsx) |
| 15 | Step-up for risky actions: `GET /api/auth/step-up` asks the IDP (`prompt=login`, `acr_values=mfa`) for a fresh authenticator code and renews this session's tokens; when the API answers 403 `mfa_required` the screen asks the Shell, which shows it in an in-page overlay (`?in=shell`: it ends on the Shell's own fixed done page); outside the Shell a small window ends on `/api/auth/step-up-done` - fixed addresses, never taken from the request - and the action is retried once | The person would have to sign out and in again; a redirect target taken from the request could be abused | Friction that pushes people to weaken MFA; open redirect | `StepUp` in [AuthController.cs](Controllers/AuthController.cs); [step-up.ts](client-app/app/lib/step-up.ts), [api.ts](client-app/app/lib/api.ts) |
| 16 | Behind an ingress that ends TLS: `X-Forwarded-For` / `X-Forwarded-Proto` are applied only from the proxies listed under `ForwardedHeaders:KnownNetworks` / `KnownProxies` (one hop; the Host header is never taken from a forwarded header); nothing listed (local runs) = none trusted | Trusting the headers from anyone would let a caller fake its address (rate limits, logs) or the scheme; trusting none behind an ingress would produce http:// sign-in redirects and endpoints | IP and scheme spoofing (CWE-348); misconfiguration (OWASP A05) | [ProxyForwardedHeaders.cs](../../../Common/WebUtilities/Security/ProxyForwardedHeaders.cs), `UseEwpForwardedHeaders` in [Program.cs](Program.cs) |
| 17 | Container image: CI builds the front end from the committed lockfile (`--frozen-lockfile`) and the BFF from source - never a local export; chiseled non-root runtime; base images pinned by tag and digest, scanned before it is published; no client secret or connection string inside (the BFF-state connection string is no longer in `appsettings.json`) | A front end exported on a developer's machine could carry anything; a secret in a layer stays in the registry | Supply-chain tampering (OWASP A08); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 18 | Pushed authorization requests required (PAR, RFC 9126 - FAPI 2.0): the sign-in request (scopes, redirect, PKCE challenge, `prompt` / `acr_values`) is pushed to the IDP with a client assertion (`private_key_jwt`); the browser is sent only `client_id` and a one-time `request_uri` (60 s). An IDP without PAR stops the sign-in - no fallback to the old way | The request travels in the browser's address bar, where an extension, a proxy log or a doctored link can read or change it | Authorization-request tampering and leakage | [Program.cs](Program.cs) (`PushedAuthorizationBehavior.Require`) |
| 19 | Client authentication by `private_key_jwt` (RFC 7523 - FAPI 2.0), no client secret: every call to the IDP (the PAR push, the code exchange, refresh and revocation) carries a new assertion signed with this app's own EC P-256 key (ES256; `iss` = `sub` = the client ID, `aud` = the IDP's issuer only, `typ` `client-authentication+jwt`, 60 s, a fresh `jti` the IDP accepts once). The private key (encrypted PEM + its password file) is read at start-up - missing or unreadable = no start-up; the IDP holds only the public key | A shared secret is known to the IDP too, travels with every token request and can be copied from a configuration file or a log; whoever holds it can act as this app | Client impersonation; leaked or replayed client credentials (OWASP A07) | `ClientAssertions` and the `OnPushAuthorization` / `OnAuthorizationCodeReceived` events in [Program.cs](Program.cs); `Oidc:ClientKey` |