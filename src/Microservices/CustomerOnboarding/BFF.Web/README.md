# Customer Onboarding MFE — Next.js SPA + ASP.NET Core 10 BFF

Technical guide to the Customer Onboarding MFE and BFF. Business requirements: [CustomerOnboarding-Requirements.md](../doc/CustomerOnboarding-Requirements.md).

## Runtime topology

```text
Shell
  -> iframe
Customer Onboarding MFE
  -> ASP.NET Core 10 BFF edge APIs
     -> Customer Onboarding API (Sophie/user access token)
     -> Documents Management API (CustomerOnboarding.BFF.To.DocumentsManagement M2M token)
```

The browser never receives either downstream access token.

## Shell menu routes

The MFE implements the exact Customer Onboarding paths already seeded in `src/Shell/MenuDB/EwpBssShellDb.sql`:

- `/v1/customers/view-all`
- `/v1/onboarding/applications/view-all`
- `/v1/onboarding/workflow/view-all`

**No Shell Menu DB SQL change is required for this package.** The onboarding form is intentionally part of the existing Onboarding Applications screen, so no fourth menu item is necessary.

## Silent login

Shell navigation points an MFE iframe at:

`/api/auth/silent-login?returnUrl=<menu-path>`

The BFF validates the return URL, and if its own cookie session is absent it starts Authorization Code + PKCE against the IDP with `prompt=none`. Because Sophie has already authenticated through the Shell, the IDP SSO session can establish the CO BFF session without another interactive login.

The CO BFF stores tokens server-side in the authentication ticket. The SPA receives only the BFF session cookie.

## Onboarding submission

The SPA sends one multipart AJAX request to:

`POST /bff/api/onboarding/applications`

The BFF then:

1. Creates `WorkflowId` and `CorrelationId` for the workflow, and sends them to the CO API as `X-Workflow-Id` / `X-Correlation-Id` / `X-Causation-Id` headers.
2. Uses the authenticated user's access token to create the Customer, with the primary residential address from the form, through the CO API (or uses the selected existing customer). The CO API applies branch scope: the address must be in the agent's branch city.
3. Creates the Onboarding Application through the CO API with the same user token.
4. Gets a dedicated Documents Management M2M access token using Client Credentials.
5. Uploads the identity proof (`KYCProof`) and the tax proof (`TaxProof`) to the DM API with the cached M2M token, stating the agent's branch in `X-Actor-Branch` (from the `organization` identity scope). Without a branch the request is refused before anything is created.
6. Reads the current application version from CO API.
7. Submits the application through CO API using the user token.
8. Returns HTTP `201 Created` to the SPA with customer, application and document identifiers.

The CO API never receives the PDF binary.

## Retry / compensation policy

- Every call to the CO and DM APIs runs through a resilience pipeline (`AddStandardResilienceHandler`): a timeout per attempt and in total (CO 10 s / 30 s; DM 30 s / 60 s for uploads), a circuit breaker, and retries for safe GET requests only.
- An API that is down, too slow or behind an open circuit gives a readable 503 at once, never a hanging page.
- M2M token acquisition has bounded exponential retry (3 attempts).
- Non-idempotent POST/DELETE operations are deliberately **not** blindly retried because the current CO and DM APIs do not expose an idempotency-key contract.
- If a later step fails after documents were uploaded in the same request, the BFF deletes those documents from DM. This is request-level cleanup, not saga compensation (see the [Saga plan §1.3](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#13-the-mfe--bff-boundary)). Any cleanup failure is logged.
- A customer or application that was already created stays in the CO database in DRAFT, so the user can retry.

## IDP configuration change

The existing Customer Onboarding BFF OIDC client is retained. Its allowed scopes are expanded to include:

- `customer-onboarding.read`
- `customer-onboarding.write`

`RequireConsent = false` is set for the CO BFF client so the Shell-to-MFE silent-login flow does not introduce a consent screen for this trusted BFF client.

## Build and run

### BFF

Run the ASP.NET Core project with the `https` profile:

```powershell
dotnet run --project .\EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.csproj --launch-profile https
```

### SPA source/export

From `client-app`:

```powershell
pnpm install
pnpm run build
pnpm run export
```

The export script copies Next.js `out` into the BFF's `wwwroot` directory.

For source-only development, `pnpm run dev` starts a standalone Next.js development server. For the Shell-integrated flow, use the exported SPA served by the ASP.NET Core BFF at `https://customer.dev.localhost:46311`.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens stay on the server (BFF pattern): the browser holds only an HttpOnly session cookie; Duende attaches the user's token to API calls | Any XSS bug could read the access and refresh tokens from JavaScript | Token theft (OWASP A07) | `AddBff`, `AddUserAccessTokenHandler` in [Program.cs](Program.cs); [api.ts](client-app/app/lib/api.ts) sends no `Authorization` header |
| 2 | Authorization code + PKCE as a confidential client; the client secret comes from configuration and the BFF refuses to start without it | An intercepted authorization code could be redeemed; a default secret could ship to production | Code interception / injection (RFC 9700) and leaked defaults | `AddOpenIdConnect` (`UsePkce`, `Oidc:ClientSecret`) in [Program.cs](Program.cs) |
| 3 | Session cookie `__Host-`, HttpOnly, Secure, `SameSite=Lax`, 30 minutes sliding | The cookie could be read by script, sent over HTTP, set by a sibling sub-domain, or ride along on cross-site requests | Session theft, session fixation and CSRF | `AddCookie` in [Program.cs](Program.cs) |
| 4 | Anti-forgery token in a header on every state-changing request | A hostile page could make the signed-in officer's browser post a decision | Cross-site request forgery (OWASP A01) | `AddAntiforgery` in [Program.cs](Program.cs); `[ValidateAntiForgeryToken]` in [OnboardingController.cs](Controllers/OnboardingController.cs), [AuthController.cs](Controllers/AuthController.cs) |
| 5 | Server-side sessions and the cookie-encryption key ring in PostgreSQL (`EwpBffStateDb`, schema `customer_onboarding_bff`, its own user); the key ring encrypted at rest (certificate, or DPAPI in Development), fail closed | A restart would sign everybody out; a stolen key ring could forge cookies; a session could not be ended from the server | Session forgery and irrevocable sessions | [PersistentDataProtection.cs](../../../Common/WebUtilities/Security/PersistentDataProtection.cs), [EwpBffStateDb.sql](../../../../db/EwpBffStateDb.sql) |
| 6 | Strict Content-Security-Policy: scripts and styles only from this BFF plus the exported inline blocks by SHA-256 hash; no `'unsafe-inline'`; `object-src 'none'`; `form-action 'self'` | An injected script or style would run (steal data, fake the screen) | Cross-site scripting and CSS injection (OWASP A03) | [ContentSecurityPolicy.cs](../../../Common/WebUtilities/Security/ContentSecurityPolicy.cs), `ContentSecurityPolicy.Build` in [Program.cs](Program.cs) |
| 7 | Framing allowed only for the Shell and the IDP (`frame-ancestors`) | Any site could frame the screens and trick a click | Clickjacking | `frameAncestors` in [Program.cs](Program.cs) |
| 8 | `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` | The browser could guess a response as script; full URLs would leak to other sites | MIME sniffing; information leakage via the Referer header | Security-header middleware in [Program.cs](Program.cs) |
| 9 | Sign-in returns only to allow-listed routes | A crafted link could bounce a signed-in user to a phishing site | Open redirect (CWE-601) | [BffRouteCatalog.cs](Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](Controllers/AuthController.cs) |
| 10 | Uploads checked before forwarding: both documents required, PDF only | Arbitrary files would be forwarded to Documents Management | Malicious file upload (first line; Documents Management verifies the signature again) | [OnboardingController.cs](Controllers/OnboardingController.cs) |
| 11 | Documents Management called with this BFF's own machine identity and the acting user's branch (`X-Actor-Branch`), accepted only from this client | Documents Management could not confine the request to the user's branch | Cross-branch document access (BOLA) | [M2MAccessTokenService.cs](Services/M2MAccessTokenService.cs), [DocumentResourceAuthorization.cs](../../DocumentsManagement/API/Authorization/DocumentResourceAuthorization.cs) |
| 12 | Shell–MFE messages accepted only from the configured Shell origin and the parent window | A hostile page that frames the MFE could pose as the Shell and steer it | Cross-origin message spoofing | [MfeShell.tsx](client-app/app/components/MfeShell.tsx) |
| 13 | Single sign-out: front-channel (`/signout-oidc` clears this session) and back-channel logout | A session would survive signing out in the Shell | Session reuse after logout | `SignOutScheme`, `MapBffManagementEndpoints` in [Program.cs](Program.cs) |
| 14 | Calls to the API: timeouts, circuit breaker, retries for GET only | A hung API would pile up requests; a retried POST could act twice | Resource exhaustion and duplicated writes | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 15 | Per-caller rate limits, host filtering (`AllowedHosts`), HSTS | One user could flood the BFF; a forged `Host` header could poison links; HTTP downgrade | Resource exhaustion (OWASP API4), host-header attacks, interception | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs); [appsettings.Development.json](appsettings.Development.json) |
| 16 | No inline styles in the front end; its own not-found page (Next.js's default one carries inline CSS) | The CSP would need `'unsafe-inline'` for styles | CSS injection | [not-found.tsx](client-app/app/not-found.tsx) |

**Not yet:** Documents Management receives the user's branch as an asserted header from this BFF's machine client, not by token exchange (planned: M5).