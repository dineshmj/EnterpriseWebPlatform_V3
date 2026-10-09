# Customer KYC BFF

NestJS BFF for the Customer KYC Next.js MFE.

## Local topology

- BFF: `https://kyc.dev.localhost:33800`
- MFE route: `/v1/kyc/cases/view-all/`
- Customer KYC API: `https://kyc-api.dev.localhost:46305`
- IdentityServer: `https://idp.dev.localhost:46392`

The browser never receives the KYC API access token. The BFF keeps the OIDC session and forwards the access token server-to-server to the KYC API.

Business requirements for this context: [CustomerKyc-Requirements.md](../doc/CustomerKyc-Requirements.md).

## What the BFF does

- Authorization Code + PKCE against the IDP client `CustomerKYC.Microservice.BFF.ClientID`, plus silent login from the Shell.
- A secure HTTP-only session cookie. The browser never receives an access token.
- Serves the static Next.js export (`KYC_BFF_STATIC_ROOT`).
- CSRF token endpoint: `GET /api/auth/csrf`. Every decision POST requires it in the `X-CSRF-Token` header.

## Endpoints

| Endpoint | Downstream |
|---|---|
| `GET /bff/api/kyc/cases` | KYC API, user token |
| `GET /bff/api/kyc/cases/:caseId` | KYC API, user token |
| `POST /bff/api/kyc/cases/:caseId/identity-verification/approve` / `reject` | KYC API, user token |
| `POST /bff/api/kyc/cases/:caseId/document-verification/approve` / `reject` | KYC API, user token |
| `GET /bff/api/kyc/cases/:caseId/identity-proof` and `/content` | Documents Management, M2M token |
| `GET /bff/api/kyc/cases/:caseId/tax-proof` and `/content` | Documents Management, M2M token |
| `GET /api/auth/login`, `silent-login`, `callback` | IDP (Authorization Code + PKCE; session regenerated at sign-in; `organization` scope gives the user's branch) |
| `POST /api/auth/logout` (CSRF token required) | Revokes the refresh token, ends the session, returns the IDP end-session URL |
| `GET /signout-oidc` | Front-channel logout target called by the IDP |
| `GET /signout-callback-oidc` | Post-logout redirect target |

Every call to Documents Management carries the signed-in user's branch (`X-Actor-Branch`), and DM only returns that branch's documents. Evidence is streamed: verified PDF inline, any other type as a download.

## HTTPS development certificate

The Node BFF must run over HTTPS because it is embedded by the HTTPS Shell. Export the existing local ASP.NET Core development certificate to a PFX file, for example:

```powershell
dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"
```

Then set:

```powershell
$env:KYC_BFF_TLS_PFX_PATH="$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx"
$env:KYC_BFF_TLS_PFX_PASSWORD="dev-password"
```

The certificate must cover `*.dev.localhost`.

## Configuration

The BFF reads environment variables only; `src/configuration/kyc-bff-options.ts` lists them all, and `.env.example` shows every name with development values. `runnow.bat` sets them for local development and starts the BFF.

The secrets `KYC_BFF_CLIENT_SECRET`, `KYC_BFF_SESSION_SECRET` and `KYC_DOCUMENTS_MANAGEMENT_M2M_CLIENT_SECRET` have **no defaults**: if any is missing, the BFF refuses to start.

## Install and run

```powershell
pnpm install
pnpm run type-check
pnpm run build
```

Build/export the MFE:

```powershell
pnpm --dir client-app install
pnpm --dir client-app run type-check
pnpm --dir client-app run export
```

Then start the BFF:

```powershell
pnpm run start
```

`ps\build\CompileAndExportBFFClients_V3.ps1` runs the MFE export and the BFF build together.

## Documents Management M2M

The BFF uses its own client-credentials identity (`Kyc.BFF.To.DocumentsManagement.M2M.ClientID`, scope `documents-management.read`). It keeps the M2M token server-side and relays document content to the browser (currently buffered in memory, not streamed).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens stay in the server-side session; the browser holds only a session cookie | Any XSS bug could read the tokens | Token theft (OWASP A07) | [oidc.service.ts](src/auth/oidc.service.ts), [api.ts](client-app/app/lib/api.ts) (no `Authorization` header) |
| 2 | Authorization code + PKCE (S256), `state` and `nonce`; a fresh session ID at sign-in | A stolen code could be redeemed; an attacker could plant a session ID before sign-in | Code interception, CSRF on the callback, session fixation | [oidc.service.ts](src/auth/oidc.service.ts) |
| 3 | Session cookie `__Host-KYC-BFF-SESSION`, HttpOnly, Secure, `SameSite=Lax`, 30 minutes | The cookie could be read by script, sent over HTTP or ride on cross-site requests | Session theft and CSRF | [main.ts](src/main.ts) |
| 4 | Anti-forgery token on every decision, compared in constant time | A hostile page could make the officer's browser post a decision | Cross-site request forgery; timing attacks on the token | [csrf.ts](src/auth/csrf.ts), [kyc-cases.controller.ts](src/controllers/kyc-cases.controller.ts) |
| 5 | Strict Content-Security-Policy: scripts and styles by hash of the export, no `'unsafe-inline'`; framed only by the Shell and the IDP | Injected scripts or styles would run; any site could frame the screens | Cross-site scripting, CSS injection, clickjacking | [content-security-policy.ts](src/security/content-security-policy.ts), [main.ts](src/main.ts) |
| 6 | Evidence served by its verified type only: PDF inline, anything else as a download, `nosniff`, framable only by this MFE | An uploaded HTML or SVG "document" could run script on this origin | Stored XSS through uploads | [kyc-cases.controller.ts](src/controllers/kyc-cases.controller.ts) |
| 7 | Request bodies validated and stripped of unknown fields (`ValidationPipe`, `whitelist`) | Unexpected fields could reach the API | Mass assignment | [main.ts](src/main.ts) |
| 8 | Sign-in returns only to allow-listed routes | A crafted link could bounce the user to a phishing site | Open redirect (CWE-601) | `safeReturnUrl` in [auth.controller.ts](src/auth/auth.controller.ts) |
| 9 | Logout revokes the refresh token; signed back-channel logout tokens (verified, one-time `jti`) end every session of the IDP session | A refresh token would keep working after logout; signing out in the Shell would leave this session alive | Session and token reuse after logout | [auth.controller.ts](src/auth/auth.controller.ts), [backchannel-logout.controller.ts](src/auth/backchannel-logout.controller.ts), [session-registry.ts](src/auth/session-registry.ts) |
| 10 | An officer's decision is sent exactly once (10-second timeout, no retry); GETs may be retried | A retried decision could arrive twice or show a misleading "already decided" | Duplicated or confusing decisions | `postOnce` in [kyc-api.service.ts](src/services/kyc-api.service.ts) |
| 11 | Secrets required, no fallbacks | The BFF could start with a default or empty secret | Leaked defaults | `required` in [kyc-bff-options.ts](src/configuration/kyc-bff-options.ts) |
| 12 | Shell–MFE messages accepted only from the Shell's origin and the parent window; own not-found page (no inline CSS) | A hostile framing page could pose as the Shell | Cross-origin message spoofing; CSS injection | [MfeShell.tsx](client-app/app/components/MfeShell.tsx), [not-found.tsx](client-app/app/not-found.tsx) |

**Not yet:** sessions are in memory (one instance only); no circuit breaker, structured logs or metrics (planned: M6). Documents Management receives the officer's branch as an asserted header from this BFF's machine client, not by token exchange (planned: M5).