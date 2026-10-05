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
| `CustomerOnboarding.Microservice.BFF.ClientID` | Interactive (confidential) | CO BFF | Present |
| `CustomerKYC.Microservice.BFF.ClientID` | Interactive (confidential) | KYC BFF | Present |
| `Accounts.Microservice.BFF.ClientID` | Interactive (confidential) | Accounts BFF | Reserved |
| `Payments.Microservice.BFF.ClientID` | Interactive (confidential) | Payments BFF | Reserved |
| ~~`DocumentsManagement.Microservice.BFF.ClientID`~~ | — | — | Removed: DM has no MFE by design |
| `CustomerOnboarding.BFF.To.DocumentsManagement.M2M.ClientID` | M2M | CO BFF → DM (`documents-management.write`) | Present |
| `Kyc.BFF.To.DocumentsManagement.M2M.ClientID` | M2M | KYC BFF → DM (`documents-management.read`) | Present |
| `CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID` | M2M | KYC Case Opening Subscriber → KYC API (`customer-kyc.write`) | Present |
| `CustomerOnboarding.OutcomeSubscriber.To.CustomerOnboardingApi.M2M.ClientID` | M2M | Onboarding Outcome Subscriber → CO API internal endpoint (`customer-onboarding.write`) | Present |
| `BSS.ApiTesting.Bruno.ClientID` | Interactive (public, PKCE) | Developer API testing with Bruno; redirect URIs `http://127.0.0.1:3000/callback` and `https://oauth.usebruno.com/callback` | Present, registered **only in Development** |

## 4. API Resources and Scopes

| API resource (audience) | Scopes |
|---|---|
| Customer Onboarding API | `customer-onboarding.read`, `customer-onboarding.write` |
| Customer KYC API | `customer-kyc.read`, `customer-kyc.write` |
| Documents Management API | `documents-management.read`, `documents-management.write` |
| Accounts API | `accounts.read`, `accounts.write` |
| Payments API | `payments.read`, `payments.write` |
| Compliance API | *(to be registered)* |

Identity scopes: `openid`, `profile`, `email`, `roles`, `organization`.

## 5. Claims Issued

| Claim | Source |
|---|---|
| `sub` | `users.subject_id` |
| `name`, `email` and other profile claims | `users` |
| `role` | `user_roles` → `roles.code` |
| `permission` | `role_permissions` → `permissions.code` (one claim per permission) |
| `employee_id`, `department`, `branch`, `branch_city`, `branch_country_code`, `region`, `employment_type`, `clearance_level` | `user_employment_profiles` (+ `branches`) |

Identity resources: `openid`, `profile`, `email`, `roles` and **`organization`** (`employee_id`, `department`, `branch`, `branch_city`, `branch_country_code`, `region`, `clearance_level`, `employment_type`). The CO and KYC BFF clients request `organization` so they know the acting user's branch. The Customer Onboarding API resource also carries `branch`, `branch_city` and `branch_country_code` in its access tokens (branch-scoped customer access).

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
| `jack.accounts` | Jack Wilson | `account_officer` | ACCOUNTS | SYD002 | 3 |
| `emily.payments` | Emily Carter | `payments_officer` | PAYMENTS | SYD002 | 4 |
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
- Each interactive client registers a front-channel logout URI **and a back-channel logout URI** (Shell and CO BFF: Duende `/bff/backchannel`; KYC BFF: `/backchannel-logout`). On end-session the IDP notifies every participating client both ways; the back-channel call (a signed logout token, server-to-server) ends the BFF session even when the browser blocks the front-channel iframes. The user-facing logout flow is owned by the [Shell](../../Shell/doc/Shell-Requirements.md#6-logout).

---

## 8. Security Requirements and Gaps

| Requirement | Status |
|---|---|
| Account lockout and login throttling | Present: 5 failed attempts lock the account for 15 minutes; login is limited to 20 requests per minute per IP |
| MFA / step-up authentication for high-risk operations | Planned |
| No username enumeration (uniform timing and response for unknown users) | Present: unknown, inactive and locked users get the same response and the same hash cost |
| A password-hasher upgrade must not lock users out (`SuccessRehashNeeded` treated as success, then rehash) | Present |
| Security headers (CSP, `frame-ancestors`, `nosniff`) on login, consent and logout pages | Present (`SecurityHeadersAttribute` now covers Razor `PageResult`) |
| Logout only by POST, with anti-forgery | Present: GET signs out only for a client-initiated logout with a valid `id_token_hint` (no prompt needed); otherwise a confirmation form is POSTed |
| Front-channel logout iframe rendered on the logout page | Present (`Pages/Account/LoggedOut`) |
| Secrets (client secrets, DB credentials) from a secret store, not compiled constants | Partial: client secrets come from configuration (`ClientSecrets:<client id>`; Development values in `appsettings.Development.json`) and the IDP refuses to start without them. Remaining: a real secret store; per-service DB credentials. |
