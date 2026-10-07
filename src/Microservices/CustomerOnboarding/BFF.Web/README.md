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

- Safe GET requests have bounded exponential retry (3 attempts) for transient HTTP failures.
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

## Security notes

- The user access token is attached server-side by Duende Access Token Management; it is never exposed to browser JavaScript.
- Both secrets of this BFF (`Oidc:ClientSecret`, `CustomerOnboardingBff:M2MClientSecret`) come from configuration. Development values are in `appsettings.Development.json`; elsewhere supply them from the environment or a secret store. The BFF refuses to start without the OIDC secret.
- Sessions are server-side (Duende BFF, stored in PostgreSQL `EwpBffStateDb`, schema `customer_onboarding_bff`, user `ewp_co_bff`); the cookie carries only a session reference. The Data Protection keys that encrypt the cookies are kept in the same schema, so a restart signs nobody out. An API call whose session has expired answers **401** (never a redirect), and the MFE signs in again silently. Requests are rate limited per person, machine client and IP (429 with `Retry-After`).
- Responses carry `Content-Security-Policy: frame-ancestors <Shell> <IDP>`, `nosniff` and a referrer policy. `ShellOrigin` can be configured.
- `POST /api/auth/logout` requires the anti-forgery token. The user-facing logout is owned by the Shell; the IDP's front-channel logout ends this session through `/signout-oidc`.
- The silent-login return URL is an allow-listed path rather than an arbitrary redirect target.
- The BFF validates PDF filename/content type before forwarding it.
- DM remains the owner of document persistence and storage; CO owns onboarding/customer business state.