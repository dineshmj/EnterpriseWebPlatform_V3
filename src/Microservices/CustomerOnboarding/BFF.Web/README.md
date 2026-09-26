# Customer Onboarding MFE — Next.js SPA + ASP.NET Core 10 BFF

This project adds the Customer Onboarding MFE to EnterpriseWebPlatform_V3.

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

1. Uses the authenticated user's access token to create the Customer through CO API.
2. Creates the Onboarding Application through CO API using the same user token.
3. Gets a dedicated Documents Management M2M access token using Client Credentials.
4. Uploads the first PDF to DM API using the M2M token.
5. Uploads the second PDF to DM API using the same cached M2M token.
6. Reads the current application version from CO API.
7. Submits the application through CO API using the user token.
8. Returns HTTP `201 Created` to the SPA with customer, application and document identifiers.

The CO API never receives the PDF binary.

## Retry / compensation policy

- Safe GET requests have bounded exponential retry (3 attempts) for transient HTTP failures.
- M2M token acquisition has bounded exponential retry (3 attempts).
- Non-idempotent POST/DELETE operations are deliberately **not** blindly retried because the current CO and DM APIs do not expose an idempotency-key contract.
- If the second document upload or final application submission fails after documents were created, the BFF attempts compensating DELETE operations against DM. Any compensation failure is logged.
- If customer/application creation succeeds and a later operation fails, the application remains available in the CO database rather than pretending the distributed operation was atomic.

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

For source-only development, `pnpm run dev` starts a standalone Next.js development server. For the Shell-integrated flow, use the exported SPA served by the ASP.NET Core BFF at `https://customer.dev.localhost:44311`.

## Security notes

- The user access token is attached server-side by Duende Access Token Management; it is never exposed to browser JavaScript.
- The DM M2M secret is in `appsettings.Development.json` only for this PoC. Move it to a secret store/environment variable for any production-like deployment.
- The silent-login return URL is an allow-listed path rather than an arbitrary redirect target.
- The BFF validates PDF filename/content type before forwarding it.
- DM remains the owner of document persistence and storage; CO owns onboarding/customer business state.
