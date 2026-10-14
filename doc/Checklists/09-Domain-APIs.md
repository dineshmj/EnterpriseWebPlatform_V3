# 09 — Domain APIs (.NET)

Deployables: `src/Microservices/<Context>/API` for Customer Onboarding, KYC, Documents Management, Compliance, Accounts, Payments, Notifications and Audit

## 1. Tokens

- [x] **JWT bearer validation:**
  - issuer from IDP metadata over HTTPS only (`RequireHttpsMetadata`);
  - audience = this API;
  - signature and lifetime;
  - `ValidTypes = ["at+jwt"]`, so an ID token never passes.

  → each `Program.cs`
- [x] **Claim names are kept as issued** (`MapInboundClaims = false`), and the role claim is `role`.

## 2. Authorization

- [x] **Deny by default:** every controller is mapped through `MapEwpApiControllers()` with the `DenyAnonymousByDefault()` fallback. Only health and metrics are anonymous. *Because* a new endpoint without an attribute must not be public. → `src/Common/WebUtilities/Security/ApiEndpointAuthorization.cs`
- [x] **Scope + role / permission per operation** (policies). The scope is the coarse boundary.
- [x] **Object level:** branch ABAC (customers, documents, KYC, compliance, accounts, payments) and relationship ReBAC (managing agent, assigned officer), checked in the database query and in the aggregate. Out-of-scope records answer 404. *Because* this prevents BOLA (OWASP API1).
- [x] **Separation of duties in the aggregates:**
  - the initiator never decides;
  - a KYC decider never decides compliance;
  - four eyes on reconciliation;
  - an unknown initiator is denied (fail closed).
- [x] **Step-up MFA** (Payments, Accounts, Compliance, KYC): `amr` mfa plus a recent `auth_time`. Start-up is refused outside Development with MFA off. → `src/Common/WebUtilities/Security/MfaStepUp.cs`
- [x] **Internal endpoints pin the caller's `client_id`** (one subscriber each), not just the scope.
- [x] **Branch and identity always come from the token, never from a header or the request body.**

## 3. Data and messaging

- [x] **State and the events it produces are saved in one transaction (Transactional Outbox);** consumed message IDs go to the Inbox in the same transaction. A copy delivered at the same moment is answered as "already processed", never failed: the unique key violation is caught, and Payments checks its Inbox again under the saga row lock. *Because* this avoids dual writes and makes redelivery harmless.
- [x] **Concurrency:** a `Version` on every aggregate, plus `SELECT … FOR UPDATE` where two people can act at once.
- [x] **Calls to external systems** (core banking, screening provider, payment network) are checked at start-up: outside Development they must be HTTPS, to a permitted host, and never to an internal address. No redirects are followed; each call has a timeout, retry and circuit breaker. *Because* this prevents SSRF. → `src/Common/WebUtilities/Security/ExternalEndpoint.cs`
- [x] **Uploads are checked by magic bytes** against an allow-list, and evidence is encrypted at rest (AES-256-GCM envelope; Documents Management).
- [x] **The audit trail is append-only** (a trigger and the grants), hash-chained, and anchored outside its database (Audit).

## 4. Errors, limits and headers

- [x] **Errors are Problem Details with a `traceId` only** (`ApiExceptionHandler`): no stack trace, SQL or exception text.
- [x] **Per-caller rate limits:**
  - per person: 600 a minute, of which at most 60 changes;
  - 120 a minute per anonymous IP;
  - 3,000 a minute per machine client, including the subscribers' internal endpoints;
  - 429 with `Retry-After`.

  Authentication runs before the limiter. The counters are per instance. → `src/Common/WebUtilities/Security/RateLimiting.cs`
- [x] **SignalR (Notifications):** at most `Notifications:MaxConnectionsPerPerson` (default 10) connections per person, counted per instance; 4 KB messages; the connection closes when the token expires.
- [x] **HTTPS only** (HSTS outside Development), and `AllowedHosts` set. Swagger exists in Development only.
- [x] **No full account numbers in logs:** BSB plus the last four digits (`LogMasking`).

## 5. Configuration and operations

- [x] **Fail closed:**
  - missing secrets, Kafka SASL settings, key rings, approval limits or alert ages stop start-up;
  - "Unlimited" must be written explicitly.
- [x] **Liveness and readiness are separate;** Degraded means "look" (Outbox backlog, screening backlog, unconfirmed payments, onboarding age, audit chain).
- [x] **Each API has its own least-privilege database login** (see 13 and 14).

## 6. Production (open)

- [ ] **mTLS** between services (R-9).
- [ ] **Network controls** for `/metrics` and the health endpoints (R-8).
- [ ] **A secret store and key vault** (R-2, R-10).
- [ ] **Deployment-wide rate limits** at a gateway or with a shared store (R-14).