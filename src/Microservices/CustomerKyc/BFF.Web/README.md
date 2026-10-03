# Customer KYC BFF

NestJS BFF for the Customer KYC Next.js MFE.

## Local topology

- BFF: `https://kyc.dev.localhost:33800`
- MFE route: `/v1/kyc/cases/view-all/`
- Customer KYC API: `https://kyc-api.dev.localhost:44305`
- IdentityServer: `https://idp.dev.localhost:44392`

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

`CompileAndExportBFFClients_V3.ps1` at the repository root runs the MFE export and the BFF build together.

## Documents Management M2M

The BFF uses its own client-credentials identity (`Kyc.BFF.To.DocumentsManagement.M2M.ClientID`, scope `documents-management.read`). It keeps the M2M token server-side and relays document content to the browser (currently buffered in memory, not streamed).
