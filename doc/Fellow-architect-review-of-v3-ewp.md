## EWP V3 Review

**Type:** Point-in-time code review (static; nothing was executed)  
**Baseline:** the codebase as it was before commit `7a4e4b8` (early October 2026)  
**Purpose of this document:** a record of findings. The findings below are kept as originally written. Their current status is tracked in the table immediately below; the owning requirements documents carry the remediation as "Gap" items.

### Remediation status

| # | Finding | Status | Notes |
|---|---|---|---|
| 1 | Customer Outbox relay stall | **Fixed** | `7a4e4b8` fixed the routing and the query filter. Both relays (CO and KYC) now also: claim rows with `FOR UPDATE SKIP LOCKED`; publish only the oldest unpublished message per aggregate; bound attempts with exponential backoff and park exhausted rows; use one idempotent `acks=all` producer. |
| 2 | Documents Management authorization is scope-only | **Fixed** | Branch-scoped object-level authorization on every operation. The list query is filtered and paged in the database. Only the CO BFF client may delete. The acting branch comes from the user token, or from `X-Actor-Branch` sent by a pinned BFF client. Remaining: delegated user context (token exchange) instead of the asserted header. |
| 3 | Stored-XSS chain on the KYC origin | **Fixed** | DM allow-lists PDF / PNG / JPEG, verifies magic bytes, and stores and serves only the verified type, as an attachment with `nosniff`. The KYC BFF streams the content, rendering only verified PDF inline and sending any other type as a download. The filename fallback is removed. The Bruno client is registered in Development only. |
| 4 | Customer Onboarding is RBAC-only; the scope policy accepts either scope | **Fixed** | The scope is enforced per operation. Branch-scoped access applies to reads, lists and writes (agent branch city = customer's primary residential city). Administrators have no write access. `SubjectId` / `BranchId` are no longer accepted from the caller. |
| 5 | Secrets and key management | **Mostly fixed** | Secrets are removed from `Common.Landscape` and come from each deployable's own configuration; components fail closed if one is missing. The developer signing key is used in Development only. Remaining: development values still live in `appsettings.Development.json` / `runnow.bat`; one PostgreSQL superuser for all services. |
| 6 | IDP hardening | **Fixed** | Account lockout, IP throttling, no username enumeration, `SuccessRehashNeeded` accepted with rehash, security headers on Razor Pages, POST-only logout, front-channel logout iframe, ROPC validator removed, refresh-token rotation. Remaining: MFA. |
| 7 | Browser and iframe boundary | **Mostly fixed** | Static parent-origin allow-list in the MFEs; CSP `frame-ancestors` / `nosniff` on the Shell and both BFFs; Shell session 30 minutes; `UseBff()` order; server-side sessions in the .NET BFFs; KYC session regenerated at login, refresh token revoked and IDP session ended at logout. Remaining: the NestJS BFF still uses the in-memory session store; `SameSite=None` cookies. |
| Medium | Circuit breakers, saga depth, SoD edge cases, uneven DDD, exception leakage, observability, document data protection | Open | Not in this remediation pass. |
| Low | README said IdentityServer 7 | **Fixed** | |
| Low | KYC BFF `.env.example` variable names differ from the code | **Fixed** | Unified on `KYC_DOCUMENTS_MANAGEMENT_*`; the default M2M client ID is corrected; secrets have no fallbacks. |
| Low | NestJS: failed discovery cached; CSRF compared with `!==`; documents buffered | **Fixed** | |
| Low | CO BFF: `/api/auth/user` returns every claim; logout is a GET | **Fixed** | |

I read 247 hand-written source files: the IDP, three APIs, both BFF stacks, the Shell, the Kafka publishers and subscriber, the SQL schemas and your four design docs. I excluded build output. This is a static review; I did not run anything, so each finding comes from code I read, and I note where I'm less certain.

The BFF token handling, the KYC maker-checker logic and the outbox write path are carefully built. The recurring gap is that the docs and comments claim more than the code delivers. The sections below take that gap one area at a time.

### Critical / High

**1\. The Customer outbox relay will stall permanently.**

-   `CustomerOutboxPublisher.cs` fetches the oldest 50 unpublished rows, then skips any row that isn't `CustomerCreated` without marking it published.
-   `CustomerDbContext` writes `OnboardingApplicationSubmitted` and `…StatusChanged` rows, and no topic or publisher exists for them.
-   Those rows pile up. Once 50 of them are the oldest unpublished rows, new `CustomerCreated` events are never fetched, and KYC cases stop being created.
-   By my count that happens after roughly 25 onboardings.
-   Fix: publish those event types, or mark them as skipped, or filter on event type in the query.
-   Both outbox relays also have these gaps:
    -   No `FOR UPDATE SKIP LOCKED`, so two instances double-publish.
    -   Retries are unbounded and there is no dead-letter handling.
    -   A failed row is skipped, so later events for the same aggregate can publish first.
    -   KYC builds a new Kafka producer on every poll cycle.
    -   Neither producer sets idempotence or `acks=all`.

**2\. Documents Management has no authorization beyond "has the scope".**

-   `GET /v1/documents` with no filter returns every document's metadata, with no paging.
-   Any token carrying `documents-management.read` can download any document.
-   `write` covers both upload and `DELETE`, so the Onboarding BFF's service credential can hard-delete any KYC document.
-   The BFFs call DM with an M2M token only, so DM never learns which human acted.
-   A service token with no user context is the confused-deputy pattern. The usual fix is RFC 8693 token exchange, or a signed delegation or actor claim.
-   The KYC BFF's "legacy fallback" (`documents-management.service.ts`) is worse. If a customer has no `KYCProof`, it lists all documents in the store and picks one by filename regex. An officer can end up reviewing another customer's identity document.

**3\. A stored-XSS chain on the KYC origin looks plausible.**

-   DM stores and replays the client-declared `Content-Type`.
-   The KYC BFF serves it as `Content-Disposition: inline`, with no `nosniff` and no CSP.
-   The Onboarding BFF only checks the *declared* type and extension. There are no magic-byte checks and no AV scanning.
-   The Bruno client is public, carries DM write scope, and is registered unconditionally. Any IDP user could upload `text/html` against a victim's `X-Business-Reference`.
-   It would then render in an officer's session on the KYC origin, where the script can read `/api/auth/csrf` and post approvals.
-   I read this from the code and didn't exploit it; it needs an attacker with an IDP account.

**4\. The Customer Onboarding API is RBAC-only, with scopes that don't discriminate.**

-   The `ApiScope` policy is `RequireClaim("scope", READ, WRITE)`, which matches either scope. A read-only token passes on write endpoints.
-   Roles are the only gate. Any `customer_service_agent` can read or edit any customer or application (BOLA). `Customer.BranchId` is never compared to the caller's branch.
-   `CreateCustomerRequest` accepts `SubjectId` and `BranchId` from the caller. That lets someone link a customer to an arbitrary identity.
-   The BFF also takes a client-supplied `CustomerId` and attaches an application to it.
-   Your own blueprint says RBAC alone is insufficient. The KYC API does better here (permission, department and clearance claims, plus the SoD check).

**5\. Secrets and key management.**

-   `Common.Landscape` compiles every client secret, URL and a "shell token signing key" into every service. The signing key is unused. It also couples your deployments.
-   `AddDeveloperSigningCredential()` is not gated by environment. `tempkey.jwk` holds a plaintext RSA private key. It's gitignored, but it's in the zip you shared.
-   The NestJS BFF falls back to a public session secret and client secret if env vars are missing. It fails open instead of refusing to start.
-   Every service connects as `postgres/admin`. The databases are separate, but the credential isn't, so isolation is only logical.
-   Seeded users use the password `<username>@bss`.
-   The Bruno client's redirect URI is a third-party hosted callback, `oauth.usebruno.com`.

**6\. IDP hardening.**

-   There is no lockout, throttling or MFA. `ValidateCredentialsAsync` returns early for unknown users, so timing reveals which usernames exist.
-   `PasswordManager` treats `SuccessRehashNeeded` as a failure. A hasher-parameter change would lock out valid users.
-   `[SecurityHeaders]` only matches `ViewResult`, but Razor Pages return `PageResult`. It never fires, so the login page has no CSP or `X-Frame-Options` and can be framed.
-   Logout runs on GET, so it is CSRF-able. It never renders the front-channel logout iframe, so the BFFs' `FrontChannelLogoutUri` is never called.
-   An ROPC validator is registered. No client allows that grant today, but it's dead code that OAuth 2.1 prohibits. Remove it.
-   The persistent "remember me" login lasts 30 days.

**7\. Browser and iframe boundary.**

-   Nothing outside the IDP sets CSP `frame-ancestors`, `X-Frame-Options` or `nosniff`.
-   Session cookies are `SameSite=None` everywhere, with CSRF tokens as the only defense.
-   The MFE decides who its parent is from `document.referrer`, and falls back to `'*'`. An attacker's page that frames the MFE becomes the "trusted parent", and the MFE posts workspace context to it. Use a static origin allowlist plus `frame-ancestors`.
-   Independent domains will also break iframe cookies, because of third-party cookie blocking. Your `*.dev.localhost` setup hides this.
-   The Shell cookie has no `ExpireTimeSpan`, so it defaults to 14 days sliding. The MFE BFFs use 30 minutes.
-   The Shell's `UseBff()` runs after `UseAuthorization()`; Duende's order is the reverse.
-   The .NET BFFs keep tokens in the cookie (`SaveTokens`). The NestJS BFF uses express-session's `MemoryStore` and never regenerates the session at login.
-   KYC logout destroys the local session only. It doesn't revoke the refresh token or end the IDP session, and it redirects to a route that doesn't exist.

### Medium

-   **Circuit breakers don't exist anywhere in the solution.** There are only hand-rolled retries.
    -   The KYC subscriber crash-loops on a poison message. The exception escapes the `BackgroundService`, the host stops, and Kafka redelivers the same message. There is no dead-letter topic.
    -   It fetches a fresh M2M token for every message and ignores `expires_in`.
    -   The NestJS BFF retries POST approve/reject on 5xx. A timeout after the server committed produces a misleading 409. The Onboarding BFF correctly refuses to retry writes, so the two BFFs disagree.
    -   The Onboarding BFF HttpClients have no timeouts, so the default is 100 seconds.
-   **The saga is one hop deep.**
    -   Nothing consumes `kyc.case.approved` or `rejected`.
    -   The BFF runs a synchronous orchestration, and its compensation hard-deletes documents. That contradicts your own saga doc on KYC retention.
    -   Compensation leaves the Customer and Application rows behind. `CustomerCreated` has already fired, so a KYC case can exist with no documents.
    -   `InboxMessage` is defined but unused.
    -   The `X-Workflow-Message-Id` header is sent and never read.
    -   Idempotency works only through the unique `customer_number`.
-   **SoD has edge cases.**
    -   The initiator check is skipped when `InitiatedByUserId` is null, which fails open.
    -   That value is asserted by the subscriber from a Kafka message, and Kafka is unauthenticated.
    -   One officer can approve both stages and complete the case alone.
-   **DDD is uneven.**
    -   Customer Onboarding has real aggregates. KYC is an anemic entity with string statuses.
    -   `CustomerDbContext` reads HTTP headers to build integration events.
    -   The BFF generates application numbers with `Random.Shared`.
-   **Exception handling leaks.** The Onboarding and DM exception handlers return `InvalidOperationException.Message` to clients in every environment.
-   **Observability is missing.** There is no OpenTelemetry, no trace context, no health checks and no rate limiting. Correlation IDs live only in payloads, not in Kafka headers. There are also no test projects in the archive.
-   **KYC document data is unprotected.** Documents sit on the local filesystem with no encryption at rest, no retention or legal hold and no read-audit trail.

### Low / hygiene

-   **Config:** `.env.example` variable names don't match what the NestJS code reads, so the M2M secret silently ends up empty. The default M2M client ID is also wrong. The README says IdentityServer 7, while the project uses 8.0.9.
-   **NestJS BFF:** a failed `Issuer.discover` is cached forever. CSRF tokens are compared with `!==`. Document bodies are fully buffered in memory.
-   **Onboarding BFF:** `/api/auth/user` returns every claim. Logout is a GET.
-   **IDP and APIs:**
    -   `ConfigureApplicationCookie` is a no-op here.
    -   Consent is required only on the Shell client.
    -   JWT validation doesn't pin `ValidTypes = at+jwt`.
    -   Duende logging is at Debug in the base settings.
    -   `AllowedHosts` is `*`.
-   **Dev infrastructure:** Kafka runs PLAINTEXT. Kafka UI is unauthenticated with dynamic config on. Schema scripts begin with `DROP TABLE` and there are no migrations.

### Your objectives, scored

| Objective | Status |
| --- | --- |
| Tokens kept off the browser (BFF) | Strong; no browser storage or `innerHTML` anywhere |
| OIDC / OAuth 2.1 | Partial (PKCE, state and nonce are right; the ROPC validator is registered, the dev key is unconditional and refresh tokens are reusable) |
| Human vs service identity | Partial (the tokens are separate, but downstream services lose the human entirely) |
| Authorization beyond RBAC | KYC yes; Onboarding and DM no |
| Transactional outbox | Atomic write is correct; the relay is not production-safe |
| Idempotency / resilience | Retries only; no circuit breakers, no DLQ, no inbox |
| Saga choreography | First hop only |
| Observability / auditability | Not yet present |

### What's done well

-   JWT validation is correct on all three APIs: audience, issuer, HTTPS metadata and no claim remapping. Controllers are deny-by-default through `MapControllers().RequireAuthorization`.
-   The KYC decision path takes a row lock, applies a conditional update and writes the outbox in one transaction. The schema backs it with CHECK constraints.
-   M2M policies pin `client_id`, and the Onboarding BFF's M2M token cache is single-flight.
-   Return URLs are allowlisted and PKCE is applied correctly.
-   DM's storage layer is protected against path traversal.

### Suggested order

1.  Fix the outbox stall.
2.  Add per-resource and per-user authorization to DM and CO, and enforce scope per action.
3.  Add a document content allowlist with magic-byte checks, `nosniff` and `attachment` disposition, and remove the KYC filename fallback.
4.  Move secrets to a store, gate the dev key and the Bruno client by environment, and add IDP lockout and security headers.
5.  Add `frame-ancestors` and an origin allowlist, and move to server-side sessions.
6.  Add a resilience library, dead-letter handling and OpenTelemetry.