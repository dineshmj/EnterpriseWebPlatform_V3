# 01 — Identity provider (Duende IdentityServer)

Deployable: `src/IDP` · Database: `EwpIdentityAccessDb` (user `ewp_idp`)

## 1. Users, roles and claims

- [x] **Users, roles and permissions come from the IDP's own database:** users, roles, permissions, role-permissions, branches, departments and employment profiles, read through `UserRepository`. *Because* the IDP is the single source of who someone is and what they may do. → `src/IDP/Data/`, `src/IDP/Repositories/UserRepository.cs`
- [x] **Claims are built per request by a profile service.** Only the claim types the client or scope asked for are added: role, permission, branch (with city and country), department, clearance level, LAN ID and name. *Because* a token should carry no more than its audience needs. → `src/IDP/Services/CustomProfileService.cs`
- [x] **Claim names are short and stable:** `sub`, `role`, `permission`, `branch`, `amr`, `auth_time`. Every API and BFF reads them by these names, with no Microsoft claim-type remapping. *Because* a renamed claim silently breaks authorization. → `CustomProfileService.cs`; `MapInboundClaims = false` in every API and BFF

## 2. Registrations (one C# file each)

- [x] **One file per API resource and per API scope** (read and write per microservice). → `src/IDP/ConfigRegistration/ApiResources/`, `ConfigRegistration/MicroserviceApiScopes/`
- [x] **One file per BFF client:** the Shell and 7 MFEs. → `ConfigRegistration/Clients/BssShell.cs`, `Clients/MFEs/`
- [x] **One file per machine client:** the 8 Kafka subscribers and the Audit Journey API's token exchange. → `Clients/M2M/`
- [x] **The Bruno API-testing client exists in Development only.** It is a public client: no secret, PKCE, no refresh token. *Because* a client without a secret must never exist where real data is. → `Clients/Bruno.cs`, `Config.GetClients(isDevelopment)`

## 3. BFF (human) clients

- [x] **They have:**
  - Authorization Code + PKCE and a client secret;
  - exact redirect and post-logout URIs;
  - front-channel and back-channel logout URIs, with the session required;
  - `offline_access` with one-time, sliding refresh tokens (1 hour);
  - `UpdateAccessTokenClaimsOnRefresh`.

  → e.g. `Clients/MFEs/MfePayments.cs`
- [x] **They get only their own scopes,** plus the few read scopes they need. For example, Payments gets `accounts.read` for its account search. *Because* a stolen BFF token should reach as little as possible.
- [x] **They do not have:** implicit or hybrid flow, `AllowAccessTokensViaBrowser`, wildcard redirect URIs, reusable refresh tokens, or a secret in code. *Because* tokens must never reach the browser or be replayable.

## 4. Machine (M2M) clients

- [x] **They have:** client credentials only, a secret from configuration, and exactly ONE write scope for the one API they call. → e.g. `Clients/M2M/PaymentsSagaReplySubscriberToPaymentsApiM2M.cs`
- [x] **They do not have:** a user-facing flow, refresh tokens, redirect URIs, or a second API's scope. The receiving API also pins the `client_id` on its internal endpoint. *Because* a stolen worker secret should open one endpoint only.
- [x] **Secrets are read per deployable** (`ClientSecretStore`). A missing secret stops the IDP from starting (fail closed). → `src/IDP/Security/ClientSecretStore.cs`

## 5. Tokens

- [x] **The IDP issues ID tokens, JWT access tokens and refresh tokens.** Access tokens are typed `at+jwt` and carry the API resource as their audience. *Because* an API must be able to refuse an ID token, or a token meant for another API.
- [x] **Delegated tokens come from token exchange (RFC 8693).**
  - An explicit allow-list says who may exchange what.
  - A token without a person is never exchanged.
  - The original `auth_time` and `amr` are kept.
  - The acting client appears in `act`.

  → `src/IDP/Security/TokenExchangeGrantValidator.cs`, `Clients/M2M/AuditJourneyApiTokenExchange.cs` (5-minute tokens)
- [x] **The signing key is a developer key in Development only.** Anywhere else a signing certificate is required, or the IDP does not start. `tempkey.jwk` is not in Git. → `src/IDP/Security/SigningCredentialExtensions.cs`

## 6. Sign-in, MFA and sessions

- [x] **Passwords are hashed with PBKDF2 and a per-user salt.** An unknown user costs the same time, through a dummy hash. *Because* a copied database must not reveal passwords, and timing must not reveal which users exist. → `src/IDP/Security/PasswordManager.cs`
- [x] **Accounts lock after 5 failures, for 15 minutes,** and sign-ins are throttled per IP (20 a minute). *Because* this slows guessing and password spraying. → `UserRepository.cs`; `Program.cs` (`login` rate limiter)
- [x] **TOTP MFA is protected at every step:**
  - the secret is encrypted with Data Protection;
  - recovery codes are stored hashed;
  - each time step and each recovery code is claimed atomically, so no code works twice;
  - failed enrolment codes count towards lockout;
  - `Mfa:Enabled` must be on outside Development.

  → `src/IDP/Security/MfaService.cs`, `Totp.cs`
- [x] **Step-up asks only for a fresh code.** With `acr_values=mfa` on an existing session, the IDP skips the password and issues a new `auth_time`. → `Pages/Account/Login`, `Pages/Account/Mfa`, `Security/PendingSignIn.cs`
- [x] **The pending sign-in is an encrypted, authenticated, short-lived `__Host-` cookie.** It is HttpOnly and Secure. *Because* a changed cookie must fail to decrypt, not grant a sign-in. → `src/IDP/Security/PendingSignIn.cs`
- [x] **Sessions are server-side;** the cookie is only a reference. Expired sessions are removed and trigger back-channel logout to every client. → `Program.cs` (`AddServerSideSessions`)

## 7. Hosting and headers

- [x] **The IDP runs on HTTPS only.** HSTS is on outside Development, and `AllowedHosts` limits the host names it answers.
- [x] **Every page has security headers:** a Content-Security-Policy (against XSS), `frame-ancestors 'none'` with X-Frame-Options (against clickjacking), and `nosniff`. The sign-in pages are never framed; step-up opens a window instead. → `src/IDP/SecurityHeadersAttribute.cs`
- [x] **IdentityServer raises its events** (success, failure, error, information) into the structured log. → `Program.cs`

## 8. Production (open)

- [ ] **Keys in a vault.** The signing certificate and the Data Protection keys move to a key vault or HSM, with rotation and backup.
- [ ] **Secrets in a secret store.** Client secrets come from a secret store, not from `appsettings.Development.json` (R-10).
- [ ] **Security dashboard.** Sign-in failures, lockouts and token-exchange refusals appear on a security dashboard.