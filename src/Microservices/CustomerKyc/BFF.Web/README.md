# Customer KYC BFF

NestJS BFF for the Customer KYC Next.js MFE.

## Local topology

- BFF: `https://kyc.dev.localhost:33800`
- MFE route: `/v1/kyc/cases/view-all/`
- Customer KYC API: `https://kyc-api.dev.localhost:44305`
- IdentityServer: `https://idp.dev.localhost:44392`

The browser never receives the KYC API access token. The BFF keeps the OIDC session and forwards the access token server-to-server to the KYC API.

## First slice implemented

- Authorization Code + PKCE against the existing Duende IdentityServer client `CustomerKYC.Microservice.BFF.ClientID`.
- Secure HTTP-only BFF session cookie.
- `GET /bff/api/kyc/cases` forwards the authenticated user's access token to the KYC API.
- Static Next.js export is served by the BFF.
- KYC work queue route is `/v1/kyc/cases/view-all/`.

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

The Shell's existing `View KYC Cases` menu item already targets `/v1/kyc/cases/view-all`, so no Shell menu change is required.


## Documents Management M2M

The KYC BFF has a dedicated client-credentials identity for Documents Management.
It requests `documents-management.read` and keeps the M2M access token server-side.

Identity Verification retrieves the KYC identity proof through:
`/bff/api/kyc/cases/{caseId}/identity-proof/content`

Document Verification retrieves the tax proof through:
`/bff/api/kyc/cases/{caseId}/tax-proof/content`

The browser never receives the Documents Management M2M token.
