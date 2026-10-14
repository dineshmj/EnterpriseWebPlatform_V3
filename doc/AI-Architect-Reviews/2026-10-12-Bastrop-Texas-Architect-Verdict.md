# Enterprise Web Platform V3 — Merciless Security & Architecture Review

**Scope:** Static review of the current codebase (EnterpriseWebPlatform\_V3-main). Nothing was executed.
**Stance:** Production-readiness lens for a banking / financial-services platform. “PoC” is not a free pass for architectural debt that would be catastrophic under real load, real attackers, or real regulators.

---

## Executive Verdict

This is one of the stronger architectural PoCs I have seen in the .NET banking space. The team has clearly internalized BFF, Duende IdentityServer, transactional outbox, saga patterns, deny-by-default authorization, and defense-in-depth. Many of the previous review’s critical findings (outbox stall, scope-only authorization, stored-XSS chain, secrets in shared libraries, refresh-token reuse, missing at+jwt validation, etc.) have been properly fixed.

That said, **it is still a PoC with production-shaped bones and several load-bearing gaps**. The remaining issues are not “nice-to-haves”; they are the difference between “impressive demo” and “something a CISO / external auditor would sign off on.”

---

## 1\. What Is Done Exceptionally Well

These are not soft compliments — they are hard, non-trivial engineering choices that most platforms get wrong.

### 1.1 Identity & Token Architecture

-   Duende IdentityServer with **server-side sessions** (cookie is only a reference). Stolen cookies become useless after logout.
-   **Token exchange (RFC 8693)** used deliberately so the human subject is preserved across service boundaries (act claim). Downstream services can still see *who* initiated the action.
-   Access tokens constrained with ValidTypes = \["at+jwt"\] (RFC 9068) on every API — ID tokens cannot be presented as access tokens.
-   Refresh tokens are TokenUsage.OneTimeOnly + UpdateAccessTokenClaimsOnRefresh = true.
-   Client secrets are configuration-driven and **fail-closed** at startup (ClientSecretStore). No secrets compiled into shared libraries.
-   MFA (TOTP / Google Authenticator) is correctly implemented: 160-bit secrets, Data-Protection-encrypted at rest, recovery codes stored only as SHA-256 hashes, constant-time comparison, replay protection via LastUsedTimeStep, lockout after failed attempts, \_\_Host- pending-sign-in cookie.
-   MFA step-up authorization for high-risk actions (payment approve/reject/retry) is present and returns a clear mfa\_required problem detail instead of a silent 401 loop.

### 1.2 BFF Pattern & Browser Security

-   Tokens never reach the browser. Cookie settings are disciplined: \_\_Host- prefix, HttpOnly, Secure, SameSite=Lax (with correct None only for OIDC correlation/nonce).
-   CSP is **hash-based** for both scripts *and* styles, computed from the actual exported Next.js HTML at startup. No unsafe-inline. This is rare and excellent.
-   Document serving: magic-byte verification (PDF/PNG/JPEG only), stored type is the verified type, X-Content-Type-Options: nosniff, forced attachment disposition. Stored-XSS chain is closed.

### 1.3 Messaging Reliability

-   Shared Kafka subscriber loop: at-least-once, **in-place retry** for transient failures (preserves partition order), dead-letter topic for permanent failures, offset commit only after success or DLQ.
-   Transactional Outbox with FOR UPDATE SKIP LOCKED, per-aggregate oldest-first publishing, exponential backoff, parking of exhausted rows, acks=all.
-   Inbox tables for idempotency on the receiving side.
-   Kafka client configuration is fail-closed for SASL credentials.

### 1.4 Authorization Model

-   Explicit separation of scopes vs permissions vs attributes vs workflow state vs SoD.
-   Object-level / branch-scoped authorization in CO, KYC, DM, Payments.
-   M2M policies pin client\_id — a stolen machine token from one subscriber cannot call another service’s write endpoints.
-   Audit API accepts **only** delegated (token-exchanged) tokens from the Journey API; a plain machine or BFF token is refused.

### 1.5 Audit Trail

-   Append-only via Kafka consumer inside the Audit API (no write HTTP endpoint).
-   Hash-chain tamper evidence with continuous monitoring and readiness degradation when the chain is broken.
-   Advisory locks for append serialization.

### 1.6 Payments Orchestration Saga

-   Proper aggregate-rooted state machine with history, timeouts, network-retry policy, compensation path, and a health check that reports *stuck* / *overdue* sagas as Degraded (not Unhealthy — correct distinction).

### 1.7 Operational Hygiene

-   Least-privilege DB users per deployable (db/EwpServiceDbUsers.sql).
-   Rate limiting (OWASP API4) applied to API surfaces.
-   Structured request logging with trace IDs, OpenTelemetry, Prometheus metrics, health endpoints split into live/ready.
-   Swagger gated to Development.

---

## 2\. Critical / High Findings (Must Fix Before Any Real Environment)

### C1. Local Docker Stack Is Still Wide Open

docker-compose.yml still runs:

-   PostgreSQL with POSTGRES\_PASSWORD: admin and port 5432 published.
-   Kafka in **PLAINTEXT** with no authentication.
-   Kafka UI with DYNAMIC\_CONFIG\_ENABLED: "true" and no authentication on port 8080.

There *is* a hardened Kafka UI config under kafka/, but the default compose path that developers will actually run is the insecure one.
**Risk:** Any developer machine or CI runner that publishes these ports is one nmap away from full data and topic compromise.
**Fix:** Make the secure configuration the *default* compose profile. PLAINTEXT and unauthenticated UI should require an explicit insecure profile and a loud warning.

### C2. Development Client Secrets Committed in Cleartext

Every BFF and every subscriber has its client secret in appsettings.Development.json. These are UUIDs, but they are still long-lived credentials sitting in the repository history.

For a PoC this is understandable. For anything that ever leaves a developer laptop it is unacceptable.
**Fix:**

-   Move all secrets to user-secrets / environment variables / a local secret store even in Development.
-   Document a rotation procedure.
-   Ensure the IDP refuses to start if any confidential client is missing a secret (already partially done — extend the check).

### C3. Document Storage Is Local Filesystem with No Encryption at Rest

LocalFileSystemDocumentStorage writes content under a root path. There is no envelope encryption, no key rotation story, no object-storage abstraction with SSE-KMS / customer-managed keys.

In a banking context, customer identity documents (KYC evidence) are highly sensitive. A disk snapshot or container escape yields plaintext PDFs.
**Fix:** Introduce an IDocumentStorage implementation backed by encrypted object storage (or at minimum application-level encryption with a key from a proper KMS). Keep the local implementation only for Development.

### C4. Metrics and Health Endpoints Are Anonymous by Design

/metrics and health endpoints are deliberately AllowAnonymous. The code comments correctly state that ingress must not expose them. That is an *operational* control, not an *architectural* one.

In practice, misconfigured ingress, service-mesh bypass, or a developer exposing a port will leak:

-   Outbox backlog sizes
-   Running / stuck / overdue payment sagas
-   Consumer group lag
-   Database connectivity status

This is reconnaissance gold.
**Fix:** Require a network policy *and* a scrape token / mTLS for /metrics. At minimum, gate metrics behind a configuration flag that defaults to off outside Development (already partially present) and document the network control as mandatory.

### C5. No Formal Circuit Breakers / Bulkheads on Outbound Calls

Subscribers and BFFs call downstream APIs and the IDP. There is retry logic for Kafka processing, but I do not see a resilience library (Polly, standard .NET resilience handlers with circuit breakers, timeouts, bulkheads) systematically applied to HTTP clients.

Under partial outage this will amplify load (retry storms) and can cascade.
**Fix:** Standardize an IHttpClientFactory + resilience pipeline (timeout → retry with jitter → circuit breaker → bulkhead) for every outbound client, especially M2M token acquisition and cross-service calls.

### C6. Shared BFF State Database

Multiple BFFs connect to EwpBffStateDb. If this is used for Data Protection keys or session-related state, a compromise of one BFF’s credentials gives access to the shared store.
**Verify** that Data Protection keys are isolated per BFF (or properly ring-isolated) and that the shared DB does not become a lateral-movement pivot.

---

## 3\. High / Medium Findings

### H1. Inter-Service Communication Is Not Forced to mTLS

All service-to-service traffic appears to rely on the ambient network. In a real deployment this must be mTLS (service mesh or explicit client certificates). The architecture does not encode this requirement in code or configuration contracts.

### H2. Kafka ACLs Are Described but Not Enforced in the Default Stack

The code assumes per-worker Kafka users with topic/group ACLs. The local Kafka container has none of that. Documentation should make it impossible to confuse “works on my machine” with “works under ACL isolation.”

### H3. Authorization Completeness Is Uneven Across Contexts

KYC and Payments show mature object-level + SoD + MFA step-up.
Documents Management and Customer Onboarding have improved dramatically, but a systematic matrix of “every command / query × every policy predicate” should be published and tested. Residual risk of a missed RequireClaim("branch", …) or missing SoD check on a new endpoint remains.

### H4. Audit Trail Has No Cryptographic Anchoring Outside the Database

The hash chain is excellent *inside* the Audit DB. There is no periodic external anchor (e.g., publishing the chain head to a WORM store, HSM, or public transparency log). A determined attacker with DB write access can rewrite history and recompute the chain.
For a banking audit log this is a material gap.

### H5. No Explicit Threat Model or STRIDE Analysis in the Repo

There is a large architectural blueprint and an authorization model, but no living threat model that maps assets → threats → mitigations → residual risk. This is the document security architects and auditors will ask for first.

### H6. Playwright Security Tests Exist but Coverage Is Unknown

There is a tst/playwright/tests/40-security/ tree. Good. Without evidence of coverage for:

-   CSRF on state-changing BFF endpoints
-   Session fixation / revocation after logout
-   MFA bypass attempts
-   Token exchange abuse
-   Document content-type confusion …the suite cannot be treated as a control.

### H7. Rate Limiting Configuration Is Present; Defaults and Per-Endpoint Tuning Are Opaque

AddEwpRateLimiting is applied. Without seeing the default limits and whether sensitive endpoints (login, MFA, payment initiation, document upload) have tighter buckets, this is only a partial control.

### H8. Logging of Authorization Failures and Security Events

Token-exchange refusals are logged. It is unclear whether every authorization failure (especially object-level and SoD failures) is emitted as a structured security event that can be alerted on. “Deny” without observability is half a control.

---

## 4\. Architectural Observations (Not Bugs, but Debt)

| Area | Observation | Severity if left |
| --- | --- | --- |
| **Database-per-context** | Correctly enforced. Good. | — |
| **DDD maturity** | Uneven (CO/KYC strong; some contexts thinner). | Medium |
| **Saga style mix** | Choreography for onboarding, orchestration for payments. Justified, but the operational model differs; runbooks must reflect that. | Low |
| **Simulators** | Core banking / payment network / screening simulators are present. Ensure they can never be deployed with production credentials or network paths. | High if misdeployed |
| **Next.js static export + BFF** | Correct for the security model, but the build → hash-CSP pipeline is fragile; a broken export silently weakens XSS defence. | Medium |
| **Observability** | Strong foundation. Missing: security-specific dashboards (authn failures, MFA lockouts, token-exchange denials, outbox age, saga stuck count). | Medium |
| **Schema evolution** | SQL scripts with DROP patterns still appear in places. Prefer migrations with expand/contract. | Medium |

---

## 5\. Security Control Scorecard (Current State)

| Control | Status | Notes |
| --- | --- | --- |
| Tokens never in browser | **Strong** | BFF pattern enforced |
| OAuth 2.1 / OIDC hygiene | **Strong** | PKCE, state, nonce, one-time refresh, at+jwt |
| MFA | **Strong** | Correct TOTP + recovery + lockout + step-up |
| Human vs machine identity | **Strong** | Token exchange + pinned client\_id |
| Object-level authorization | **Good → Strong** | Context-dependent |
| SoD | **Good** | Present in KYC / Payments |
| Transactional Outbox | **Strong** | Production-shaped |
| Inbox / idempotency | **Strong** |  |
| Dead-letter + retry | **Strong** |  |
| Document content safety | **Strong** | Magic bytes + nosniff + attachment |
| CSP | **Strong** | Hash-based, no unsafe-inline |
| Secrets management | **Partial** | Fail-closed, but Dev secrets in repo |
| Encryption at rest (documents) | **Weak** | Local FS only |
| Network encryption (mTLS) | **Not encoded** | Assumed operational |
| Kafka authn/ACLs (local) | **Weak** | PLAINTEXT default |
| Audit integrity | **Good** | Hash chain; no external anchor |
| Rate limiting | **Present** | Tuning unclear |
| Circuit breakers | **Missing / uneven** |  |
| Threat model | **Missing** |  |

---

## 6\. Recommended Fix Order (Ruthless Prioritization)

1.  **Secure the local stack by default** (C1) — stop training developers on insecure defaults.
2.  **Remove cleartext Dev secrets from the repository** (C2).
3.  **Encrypt document storage** (C3) — this is customer PII/KYC data.
4.  **Add resilience pipelines** to all outbound HTTP (C5).
5.  **Externalize audit chain heads** (H4).
6.  **Publish a living threat model** and map every control to it (H5).
7.  **Harden metrics/health exposure** (C4).
8.  Complete the authorization matrix tests and Playwright security suite (H3, H6).
9.  Encode mTLS / network policy requirements as deploy-time contracts (H1, H2).

---

## 7\. Final Assessment

You have built a **serious reference architecture**. The security thinking is visible in the code, not just in the documents. Previous review findings were largely closed with real engineering, not checkboxes.

The remaining gaps are classic “last 20 % that takes 80 % of the effort” items: secrets hygiene, encryption at rest, network-level controls, resilience under partial failure, and audit integrity that survives a compromised database.

Treat the current codebase as **architecture that has earned the right to be hardened**, not as something that is already production-ready. With the critical and high items above closed, this would be one of the more defensible distributed banking platforms a regulator would see.

If you want a follow-up, I can produce:

-   a concrete threat-model skeleton mapped to the current components, or
-   a prioritized remediation backlog with acceptance criteria for each finding.