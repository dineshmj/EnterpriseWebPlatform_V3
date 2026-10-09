# Identity Provider (IDP) — Requirements

**Component:** EnterpriseWebPlatform.IdentityServer  
**Subdomain type:** Generic (identity and access)  
**Status:** Present

Platform-wide authorization mechanics live in [Authorization-Model.md](../../../doc/Authorization-Model.md). This document owns what the IDP issues and stores, and its demo data.

---

## 1. Purpose and Boundary

The IDP authenticates humans and services and issues the tokens and claims that every other component relies on.

| Owns | Does not own |
|---|---|
| Authentication (interactive login, consent, logout, SSO session) | Customers, KYC cases, accounts, payments — never business master data |
| Client registrations, API resources and scopes | Resource attributes (branch of a customer, risk of a case) — owned by each context |
| Users, roles, permissions, role–permission mapping | Business authorization decisions — made by each BFF and API |
| User attributes for ABAC (employment profile) | |
| | ReBAC relationships (who manages a customer, who is assigned a case) — owned by the context that owns the resource, see [Authorization-Model §6](../../../doc/Authorization-Model.md#6-rebac--relationship-based-access-control) |

### Deployable component

| Component | Location | Technology |
|---|---|---|
| IDP | `src/IDP` | Duende IdentityServer 8 (8.0.9) on ASP.NET Core 10; Razor Pages for login, consent and logout |
| Database | `EwpIdentityAccessDb` (`IdentityAccessDB/IdentityAccessDb.sql`) | PostgreSQL |

---

## 2. Protocol Requirements

| Requirement | Status |
|---|---|
| Every interactive client uses Authorization Code + PKCE, with exact redirect URIs | Present |
| Machine clients use Client Credentials, pinned by `client_id`, with the narrowest scopes | Present |
| No implicit flow and no Resource Owner Password Credentials (OAuth 2.1) | Present (the ROPC validator has been removed) |
| A stable, opaque `sub` (a GUID `subject_id`), never a username | Present |
| Refresh tokens only where a BFF needs them; rotation (one-time use) | Present (`TokenUsage.OneTimeOnly`) |
| Consent where a user meaningfully delegates; none for first-party BFF silent login | Present (Shell requires consent; MFE BFFs do not) |
| Signing keys from a key store, rotated; developer keys only in Development | Present: developer key only in Development; elsewhere `SigningCredential:CertificatePath` / `CertificatePassword` are required (`Security/SigningCredentialExtensions.cs`). Target: automatic key rotation. |

---

## 3. Client Catalogue

| Client ID | Kind | Used by | Status |
|---|---|---|---|
| `BSS.Shell.BFF.ClientID` | Interactive (confidential) | Shell BFF | Present |
| `CustomerOnboarding.Microservice.BFF.ClientID` | Interactive (confidential) **+ token exchange** | CO BFF; exchanges the agent's token for Documents Management (`documents-management.write`) | Present |
| `CustomerKYC.Microservice.BFF.ClientID` | Interactive (confidential) **+ token exchange** | KYC BFF; exchanges the officer's token for Documents Management (`documents-management.read`) | Present |
| `Compliance.Microservice.BFF.ClientID` | Interactive (confidential) | Compliance BFF | Present |
| `Accounts.Microservice.BFF.ClientID` | Interactive (confidential) | Accounts BFF | Present |
| `Payments.Microservice.BFF.ClientID` | Interactive (confidential) | Payments BFF | Present |
| `Audit.Microservice.Web.ClientID` | Interactive (confidential) **+ token exchange** | Audit web BFF (Next.js): signs the person in, then exchanges their token for one aimed at the Audit Journey API (`audit-journey.read`) | Present (front end in 6e-3) |
| `Audit.JourneyApi.ClientID` | **Token exchange only** (no client credentials) | Audit Journey API (NestJS) → Audit API (`audit.read`) and Payments API (`payments.read`), always for a person | Present |
| ~~`DocumentsManagement.Microservice.BFF.ClientID`~~ | — | — | Removed: DM has no MFE by design |
| ~~`CustomerOnboarding.BFF.To.DocumentsManagement.M2M.ClientID`~~ | — | — | Removed: the CO BFF exchanges the agent's token instead |
| ~~`Kyc.BFF.To.DocumentsManagement.M2M.ClientID`~~ | — | — | Removed: the KYC BFF exchanges the officer's token instead |
| `CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID` | M2M | KYC Case Opening Subscriber → KYC API (`customer-kyc.write`) | Present |
| `CustomerOnboarding.OutcomeSubscriber.To.CustomerOnboardingApi.M2M.ClientID` | M2M | Onboarding Outcome Subscriber → CO API internal endpoint (`customer-onboarding.write`) | Present |
| `Compliance.CaseOpeningSubscriber.To.ComplianceApi.M2M.ClientID` | M2M | Compliance Case Opening Subscriber → Compliance API (`compliance.write`) | Present |
| `DocumentsManagement.InvalidationSubscriber.To.DocumentsManagementApi.M2M.ClientID` | M2M | Document Invalidation Subscriber → DM (`documents-management.write`) | Present |
| `Accounts.ApplicationOpeningSubscriber.To.AccountsApi.M2M.ClientID` | M2M | Account Application Opening Subscriber → Accounts API (`accounts.write`) | Present |
| `Accounts.CommandSubscriber.To.AccountsApi.M2M.ClientID` | M2M | Accounts Command Subscriber → Accounts API (`accounts.write`) | Present |
| `Payments.SagaReplySubscriber.To.PaymentsApi.M2M.ClientID` | M2M | Payments Saga Reply Subscriber → Payments API (`payments.write`) | Present |
| `Notifications.Subscriber.To.NotificationsApi.M2M.ClientID` | M2M | Notifications Subscriber → Notifications API (`notifications.write`) | Present |
| `BSS.ApiTesting.Bruno.ClientID` | Interactive (public, PKCE) | Developer API testing with Bruno; redirect URIs `http://127.0.0.1:3000/callback` and `https://oauth.usebruno.com/callback` | Present, registered **only in Development** |

## 4. API Resources and Scopes

| API resource (audience) | Scopes |
|---|---|
| Customer Onboarding API | `customer-onboarding.read`, `customer-onboarding.write` |
| Customer KYC API | `customer-kyc.read`, `customer-kyc.write` |
| Documents Management API | `documents-management.read`, `documents-management.write` |
| Accounts API | `accounts.read`, `accounts.write` |
| Payments API | `payments.read`, `payments.write` |
| Compliance API | `compliance.read`, `compliance.write` |
| Notifications API | `notifications.read`, `notifications.write` |
| Audit API | `audit.read` (delegated tokens only) |
| Audit Journey API | `audit-journey.read` (delegated tokens only) |

Identity scopes: `openid`, `profile`, `email`, `roles`, `organization`.

### Token exchange (RFC 8693, delegation)

Grant `urn:ietf:params:oauth:grant-type:token-exchange` (`Security/TokenExchangeGrantValidator.cs`). A service presents the access token it received (`subject_token`) and receives a new one for the next service: the **person stays the subject** (their roles, permissions and branch are re-read by the profile service at every exchange; a deactivated person's exchange fails), and the requesting client is added to the **`act`** claim, nested when the token was already delegated. Who may exchange what is an explicit allow-list (fail closed): the Audit web BFF only its own sign-in tokens; the Audit Journey API only tokens the Audit web BFF exchanged for it. A token without a person (`sub`) is never exchanged. Used by the Audit context ([Audit Journey API README](../../Microservices/Audit/JourneyApi/README.md)).

## 5. Claims Issued

| Claim | Source |
|---|---|
| `sub` | `users.subject_id` |
| `name`, `email` and other profile claims | `users` |
| `role` | `user_roles` → `roles.code` |
| `permission` | `role_permissions` → `permissions.code` (one claim per permission) |
| `employee_id`, `lan_id`, `department`, `branch`, `branch_city`, `branch_country_code`, `region`, `employment_type`, `clearance_level` | `user_employment_profiles` (+ `branches`) |

**Staff identity.** Every staff user has a random subject ID (`sub`, a version 4 GUID: the identity that records and every rule use) and a **LAN ID** (`lan_id`, the label screens show). LAN IDs follow the "filas" rule: two letters of the first name and three of the last, lowercase letters only, with a digit appended on a collision (Sophie Mitchell → `somit`, Ethan Parker → `etpar`, Grace Walsh → `grwal`). The IDP issues them; nothing else derives them. In production the LAN ID is the directory (Active Directory / Entra ID) sign-in name; the demo signs in with role-named user names (`sophie.cs`, `ethan.kyc`, …) so an audience can follow the personas, and those stay `preferred_username`. The Customer Onboarding, KYC, Compliance, Accounts and Payments API resources carry `lan_id` in their access tokens. Customer Service Agents hold `payment.initiate` and `payment.view` (they capture payments for customers; payments officers approve them).

Identity resources: `openid`, `profile`, `email`, `roles` and **`organization`** (`employee_id`, `lan_id`, `department`, `branch`, `branch_city`, `branch_country_code`, `region`, `clearance_level`, `employment_type`). The CO and KYC BFF clients request `organization` so they know the acting user's branch. The Customer Onboarding API resource also carries `branch`, `branch_city` and `branch_country_code` in its access tokens (branch-scoped customer access).

---

## 6. Demo Data

Demo identities are fictitious and for local development only. **Password convention: `<username>@bss`.**

| Username | Name | Role(s) | Department | Branch | Clearance |
|---|---|---|---|---|---:|
| `customer.demo` | Liam Taylor | `customer` | — | — | — |
| `sophie.cs` | Sophie Mitchell | `customer_service_agent` | CUSTOMER_SERVICE | SYD001 | 2 |
| `liam.kyc` | Liam Anderson | `kyc_officer` | KYC | SYD001 | 3 |
| `ethan.kyc` | Ethan Parker | `kyc_officer` | KYC | SYD001 | 3 |
| `noah.kyc` | Noah Hughes | `kyc_officer` | KYC | SYD001 | 3 |
| `olivia.compliance` | Olivia Bennett | `compliance_officer` | COMPLIANCE | SYD001 | 4 |
| `grace.compliance` | Grace Walsh | `compliance_officer` (senior) | COMPLIANCE | SYD001 | 5 |
| `jack.accounts` | Jack Wilson | `account_officer` | ACCOUNTS | SYD001 | 3 |
| `emily.payments` | Emily Carter | `payments_officer` | PAYMENTS | SYD001 | 4 |
| `daniel.ops` | Daniel Cooper | `operations_administrator` | OPERATIONS | BNE001 | 4 |
| `sarah.audit` | Sarah Collins | `auditor` | AUDIT | ADL001 | 5 |
| `platform.admin` | Michael Turner | `platform_administrator` + `operations_administrator` | IT | PER001 | 5 |
| `mia.cs` | Mia Robinson | `customer_service_agent` | CUSTOMER_SERVICE | MEL001 | 2 |

All staff are `FULL_TIME`. `ethan.kyc` and `noah.kyc` are deliberately unassigned, so the KYC work queue can demonstrate "first officer to decide wins".

**Branches** (city, country; region = state): `SYD001` Sydney CBD and `SYD002` Sydney North (Sydney, AU, NSW); `MEL001` Melbourne Central (Melbourne, AU, VIC); `BNE001` Brisbane City (Brisbane, AU, QLD); `ADL001` Adelaide City (Adelaide, AU, SA); `PER001` Perth City (Perth, AU, WA).

**Branch-scope demonstration:** `sophie.cs` (Sydney) and `mia.cs` (Melbourne) are both Customer Service Agents. Each can onboard and see only customers with a primary residential address in their own branch city. Applications record the branch they were opened in, and KYC officers see only their own branch's cases: the KYC officers are in Sydney, so they review Sophie's onboardings, while Mia's would wait for a Melbourne officer. Documents uploaded by Mia stay invisible to them as well.

**Clearance-by-risk demonstration (CMP):** approving a compliance case needs clearance 3 (LOW risk), 4 (MEDIUM) or 5 (HIGH, a sanctions MATCH). `olivia.compliance` (4) can therefore only reject or hold a HIGH-risk case; `grace.compliance` (5) can approve it.

**ReBAC demonstration** (relationships live in the business contexts, not here):
- The agent who creates a customer **manages** it: only `sophie.cs` can change her customers or open their applications.
- The first KYC officer to decide a case (or to claim it) is **assigned to** it. If `ethan.kyc` approves the identity stage, `noah.kyc` is refused the document stage until Ethan releases the case.

**Departments:** CUSTOMER_SERVICE, KYC, COMPLIANCE, ACCOUNTS, PAYMENTS, OPERATIONS, AUDIT, IT.

Role → permission mappings: see the permission tables in each context's requirements document and [Authorization-Model §3.2](../../../doc/Authorization-Model.md#32-permissions).

---

## 7. Session and Logout

- The IDP holds the SSO session that lets MFE BFFs sign in silently (`prompt=none`) after the Shell login.
- **Two-step sign-in (MFA), off by default.** With `Mfa:Enabled` true, the password is only the first factor: the person is not signed in until the 6-digit code from Google Authenticator (or a recovery code) is entered (`Pages/Account/Mfa`). Someone without an authenticator enrols first (`Pages/Account/MfaSetup`: QR code or setup key, one confirming code, then 10 recovery codes shown once). In between, an encrypted 5-minute cookie remembers who passed the password. The SSO session records `amr` = `pwd otp mfa`, which every token carries; the silent sign-ins of the MFE BFFs inherit it. TOTP: RFC 6238, HMAC-SHA1, 6 digits, 30 s, ±30 s drift (`Security/Totp.cs`, checked against the RFC's test vectors).
- **What the IDP remembers survives a restart and is shared by instances.** Refresh tokens (stored by a hash of the handle; details encrypted), pushed authorization requests and Duende's signing keys live in Duende's operational store, and the IDP's Data Protection keys (its cookies, the encrypted columns) next to them: `EwpIdentityAccessDb`, schema `identity_server`, used only by `ewp_idp`. Expired rows are removed hourly.
- Each interactive client registers a front-channel logout URI **and a back-channel logout URI** (Shell and CO BFF: Duende `/bff/backchannel`; KYC BFF: `/backchannel-logout`). On end-session the IDP notifies every participating client both ways; the back-channel call (a signed logout token, server-to-server) ends the BFF session even when the browser blocks the front-channel iframes. The user-facing logout flow is owned by the [Shell](../../Shell/doc/Shell-Requirements.md#6-logout).

---

## 8. Security Requirements and Gaps

| Requirement | Status |
|---|---|
| Account lockout and login throttling | Present: 5 failed attempts lock the account for 15 minutes; login is limited to 20 requests per minute per IP |
| MFA / step-up authentication for high-risk operations | Present, switched off by default (`Mfa:Enabled`): TOTP with Google Authenticator for every user; enrolment at the next sign-in (QR code, 10 single-use recovery codes); the secret encrypted with the IDP's key ring, recovery codes hashed; wrong codes count towards the lockout; a code is never accepted twice. Tokens carry `amr` (`pwd`, or `pwd otp mfa`); officer decisions and operations' retry require `mfa` while it is on (step-up, 403 `mfa_required`) |
| No username enumeration (uniform timing and response for unknown users) | Present: unknown, inactive and locked users get the same response and the same hash cost |
| A password-hasher upgrade must not lock users out (`SuccessRehashNeeded` treated as success, then rehash) | Present |
| Security headers (CSP, `frame-ancestors`, `nosniff`) on login, consent and logout pages | Present (`SecurityHeadersAttribute` now covers Razor `PageResult`) |
| Logout only by POST, with anti-forgery | Present: GET signs out only for a client-initiated logout with a valid `id_token_hint` (no prompt needed); otherwise a confirmation form is POSTed |
| Front-channel logout iframe rendered on the logout page | Present (`Pages/Account/LoggedOut`) |
| Refresh tokens, PAR requests and signing keys survive a restart and are shared by instances | Present: Duende operational store in PostgreSQL (schema `identity_server`), hourly clean-up of expired rows |
| Key ring encrypted at rest; no start-up with readable keys | Present: certificate from `DataProtection:CertificatePath` (required outside Development), DPAPI in Development on Windows; otherwise the IDP refuses to start |
| Secrets (client secrets, DB credentials) from a secret store, not compiled constants | Partial: client secrets come from configuration (`ClientSecrets:<client id>`; Development values in `appsettings.Development.json`) and the IDP refuses to start without them. Remaining: a real secret store; per-service DB credentials. |