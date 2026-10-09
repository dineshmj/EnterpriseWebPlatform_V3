# Enterprise Web Platform V3
## Architectural Vision, Enterprise Capability Blueprint & Security Roadmap

**Document status:** Living architectural blueprint  
**Version:** 3.x  
**Domain:** Banking / Financial Services  
**Audience:** Solution, application and security architects; developers; DevSecOps and platform engineers; technical reviewers  
**Last updated:** October 2026

This document owns the **platform-wide** architecture: vision, principles, landscape, context map, DDD and deployability rules, cross-cutting security and operational targets, the capability matrix and the roadmap. Context-specific requirements live with each component. The document map is in the root [README.md](../README.md#documentation-map).

---

## 1. Purpose

Enterprise Web Platform V3 (EWP V3) is a deliberately engineered **reference architecture for a modern banking-services platform**. It shows how a financial institution can build a **secure, independently deployable, observable, resilient and auditable distributed application** in which:

- business capabilities are separated into bounded contexts, each governed by Domain-Driven Design and owning its own data;
- each component is independently deployable and maintainable, so the system never degrades into a distributed monolith;
- human authentication and machine authentication are distinct, and authorization goes far beyond RBAC (ABAC, ReBAC, workflow state, Separation of Duties);
- browser applications are protected by BFF security boundaries and composed by a business-neutral Shell;
- bounded contexts integrate asynchronously through Kafka, with Transactional Outbox, idempotent consumers and sagas;
- workflow identity, causality and the accountable human are preserved across asynchronous boundaries;
- users receive real-time notifications only for what they are entitled to see;
- security, observability and auditability are architectural concerns, not afterthoughts.

The banking domain is used because it creates realistic pressure: onboarding, KYC, compliance, account opening, payments, documents, approvals, audit, sensitive personal data and long-running workflows.

> **Important:** EWP V3 is an architectural PoC, not a production banking platform and not a declaration of regulatory compliance (see §20).

---

## 2. Architectural Principles

1. Bounded-context ownership over shared domain models.
2. Database per bounded context; no cross-context database access.
3. Business rules live in the domain model of the owning context, not in UIs, BFFs or infrastructure.
4. Every deployable component can be built, tested, released and rolled back on its own.
5. The Shell composes experiences; it never becomes a business aggregator or a saga coordinator.
6. The BFF is the browser-facing security boundary; it never becomes a generic proxy or a distributed-transaction coordinator.
7. Human identity is different from service identity.
8. Authorization is enforced server-side, deny-by-default, and beyond RBAC.
9. Asynchronous integration assumes at-least-once delivery; consumers are idempotent.
10. Business state changes and outgoing events commit together (Transactional Outbox).
11. Distributed workflows propagate workflow, correlation and causation identity, and the accountable human.
12. Failures are expected: retries are bounded and paired with timeouts and circuit breakers; business failures are compensated, not retried.
13. Auditability is a first-class business requirement.
14. Sensitive data is minimized, protected and retained only as required.
15. Security is layered: identity, authorization, data, network, application, infrastructure, operations.
16. Observability spans synchronous and asynchronous boundaries.
17. Fail safely, never open.
18. The UI may guide a user; it is never the security boundary.
19. Every important decision has a clear owning component and document.

---

## 3. Status Legend

Used by every EWP V3 document:

| Status | Meaning |
|---|---|
| **Present** | Implemented and demonstrable in the current codebase |
| **Partial** | Implemented in part, or in some contexts only |
| **In Progress** | Actively being implemented |
| **Planned** | Intended for V3 |
| **Target** | An enterprise capability the architecture should eventually demonstrate |
| **Gap** | Contradicts a stated principle or requirement today; needs remediation |
| **Production Concern** | Required for a real bank, intentionally outside the PoC's immediate scope |

---

## 4. Landscape

```text
                               ┌──────────────────────────────┐
                               │ IDP — Duende IdentityServer 8 │
                               └──────────────┬───────────────┘
                                 OIDC / OAuth 2.1 (Code+PKCE, Client Credentials)
                                              │
┌─────────────────────────────────────────────▼──────────────────────────────────────────┐
│ BSS Shell (Next.js SPA + ASP.NET Core BFF) — menu, workspace, logout, notification view │
└───────┬──────────────────────────────┬─────────────────────────────┬───────────────────┘
        │ iframe                       │ iframe                      │ iframe
┌───────▼──────────────┐      ┌────────▼─────────────┐      ┌────────▼────────────────────┐
│ Customer Onboarding  │      │ Customer KYC         │      │ Compliance, Accounts        │
│ MFE + BFF (.NET)     │      │ MFE + BFF (NestJS)   │      │ MFE + BFF (.NET) · Payments │
│   │ user token       │      │   │ user token       │      └─────────────────────────────┘
│ CO API ─ EwpCustomerDb      │ KYC API ─ EwpKycDb   │
│   │ Outbox            │      │   │ Outbox (in-proc) │
└───┼──────────┬───────┘      └───┼────────▲─────────┘
    │          │ M2M (write)       │        │ M2M
    │          ▼                   │        │
    │   ┌──────────────────────┐   │  ┌─────┴──────────────┐
    │   │ Documents Management │◄──┼──┤ KYC BFF M2M (read) │
    │   │ API ─ EwpDocsMgmtDb  │   │  └────────────────────┘
    │   └──────────────────────┘   │
    ▼                              ▼
CustomerOutboxPublisher ──► Kafka ◄── KYC Outbox relay
                              │
                              ▼
                     KycCaseOpeningSubscriber ──M2M──► KYC API
                     ComplianceCaseOpeningSubscriber ──M2M──► Compliance API ─ EwpComplianceDb
                     AccountApplicationOpeningSubscriber ──M2M──► Accounts API ─ EwpAccountsDb
                     OnboardingOutcomeSubscriber ──M2M──► CO API
                     AccountsCommandSubscriber ──M2M──► Accounts API      (Payments saga: funds commands)
                     PaymentsSagaReplySubscriber ──M2M──► Payments API ─ EwpPaymentsDb   (replies)
                     NotificationsSubscriber ──M2M──► Notifications API ─ EwpNotificationsDb ──SignalR──► Shell

External systems (simulated): screening provider ◄── Compliance API, core banking ◄── Accounts API,
payment network ◄── Payments API. BFF sessions and keys: EwpBffStateDb (one schema per .NET BFF).
```

---

## 5. Bounded Contexts and Context Map

| Bounded context | Subdomain | Status | Requirements |
|---|---|---|---|
| Customer Onboarding | Core | Present | [CustomerOnboarding-Requirements.md](../src/Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md) |
| Customer KYC | Core | Present (first slice) | [CustomerKyc-Requirements.md](../src/Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md) |
| Compliance | Core | Present | [Compliance-Requirements.md](../src/Microservices/Compliance/doc/Compliance-Requirements.md) |
| Accounts | Core (simplified) | Present | [Accounts-Requirements.md](../src/Microservices/Accounts/doc/Accounts-Requirements.md) |
| Payments | Core | Present (orchestrated saga, assisted-channel screens, approval by tier and clearance, notifications) | [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md) |
| Documents Management | Generic / supporting | Present | [DocumentsManagement-Requirements.md](../src/Microservices/DocumentsManagement/doc/DocumentsManagement-Requirements.md) |
| Identity and access | Generic | Present | [IDP-Requirements.md](../src/IDP/doc/IDP-Requirements.md) |
| Composition (not a business context) | — | Present | [Shell-Requirements.md](../src/Shell/doc/Shell-Requirements.md) |
| Audit | Generic / supporting | Present (tamper-evident trail from Kafka; auditor screens in the customer's pattern: Next.js light BFF → NestJS Journey API → Domain APIs, token exchange at every hop) | [Audit API README](../src/Microservices/Audit/API/README.md) |
| Notifications | Generic / supporting | Present (including deep links from a notification) | [Notifications API README](../src/Microservices/Notifications/API/README.md), [Shell-Requirements.md §7](../src/Shell/doc/Shell-Requirements.md#7-workflow-notifications) |

### Context map

```text
Customer Onboarding ──(published events)──► Customer KYC ──► Compliance ──► Accounts
        ▲                                         │               │             │
        └──────────── decisions as events ────────┴───────────────┴─────────────┘

CO BFF, KYC BFF ──(Open Host Service, token exchange for the person)──► Documents Management
Payments orchestrator ──(commands / replies)──► Accounts
Every component ──(conformist)──► IDP claims and scopes
```

| Relationship | Pattern |
|---|---|
| CO → KYC → Compliance → Accounts | **Published Language** (integration events in the [Event Catalogue](Integration-Event-Catalogue.md)). Each downstream subscriber acts as an **anti-corruption layer**: it translates a foreign event into its own command, so no foreign model enters the domain. |
| Contexts → Documents Management | **Open Host Service**: a generic, business-agnostic API |
| Payments ↔ Accounts | **Customer–supplier**, coordinated by the Payments saga orchestrator |
| Everyone → IDP | **Conformist**: all components accept the IDP's claim and scope vocabulary |

---

## 6. Domain-Driven Design

### 6.1 Strategic rules

1. A bounded context owns its model, its language, its database and its deployables. Two contexts may use the same word (e.g. "Customer") with different meanings; they never share the class.
2. Contexts reference each other's entities **by business identifier** (customer number, application number, case ID), never by foreign key or shared type.
3. Cross-context collaboration happens only through published integration events or explicit APIs.
4. Each context keeps a ubiquitous-language glossary in its requirements document.

### 6.2 Tactical building blocks

| Building block | Rule | Reference example |
|---|---|---|
| Aggregate | The consistency boundary. All invariants are enforced by its methods. Other aggregates are referenced by ID only. | `Customer`, `OnboardingApplication` (Customer Onboarding) |
| Value object | Immutable, validated on creation, equality by value | `EmailAddress`, `PhoneNumber`, `CustomerNumber`, `PostalAddress` |
| Entity | Identity within an aggregate | `CustomerAddress` |
| Domain event | Raised by an aggregate when a business fact happens. In-process. | `CustomerCreatedDomainEvent` |
| Integration event | A published contract derived from a domain event, written to the Outbox in the same transaction | `CustomerCreated` on `customer.created` |
| Repository / unit of work | Persistence of aggregates; one transaction per command | `ICustomerRepository`, `IApplicationUnitOfWork` |
| Command / query handlers (CQRS) | Commands change one aggregate; queries read projections and never change state | `SubmitOnboardingApplicationCommandHandler` |
| Optimistic concurrency | Every aggregate carries a version; stale updates are rejected | `OnboardingApplication.Version` |

### 6.3 Layering inside a context's API

```text
API             controllers, request models, authorization policies   → depends on Application
Application     commands, queries, handlers, abstractions             → depends on Domain
Domain          aggregates, value objects, domain events, rules       → depends on nothing
Infrastructure  EF Core, Outbox / Inbox, Kafka, storage, external     → implements Application abstractions
```

The domain layer has no knowledge of HTTP, EF Core, Kafka or the IDP.

### 6.4 DDD maturity per context

| Context | Maturity | Main gap |
|---|---|---|
| Customer Onboarding | Full tactical DDD: `Customer` and `OnboardingApplication` aggregates (reference by ID only), value objects (`PersonName`, `EmailAddress`, `PostalAddress`…), domain events, explicit status codes, injected clock; event translation in a dedicated mapper | Cross-context references use database IDs (Session B: GUID references) |
| Documents Management | `Document` aggregate root with value objects (`BranchCode`, `FileName`, `ContentHash`), the content-type policy in the domain, a `DocumentUploaded` domain event | Events are not published (no consumer yet); the document is immutable by design |
| Customer KYC | Full tactical DDD: `KycCase` aggregate (`DecideStage`), `VerificationStage` value object (EF complex type), typed statuses, domain events, SoD in the aggregate (fails closed), optimistic `version` + row lock; Application layer (commands, queries) and a dedicated integration-event mapper | — |

---

## 7. Independent Deployability

### 7.1 Deployable units

| Deployable | Context | Runtime | Database | Status |
|---|---|---|---|---|
| IDP | Identity | ASP.NET Core 10 + Duende IdentityServer 8 | `EwpIdentityAccessDb` | Present |
| Shell BFF + SPA | Composition | ASP.NET Core 10 + Next.js | `EwpBssShellDb` | Present |
| CO BFF + MFE | Customer Onboarding | ASP.NET Core 10 + Next.js | — | Present |
| CO API | Customer Onboarding | ASP.NET Core 10 | `EwpCustomerDb` | Present |
| CustomerOutboxPublisher | Customer Onboarding | .NET worker | `EwpCustomerDb` (Outbox table only) | Present |
| OnboardingOutcomeSubscriber | Customer Onboarding | .NET worker | — (records outcomes through the CO API) | Present |
| KYC BFF + MFE | Customer KYC | NestJS + Next.js | — | Present |
| KYC API (+ in-process Outbox relay) | Customer KYC | ASP.NET Core 10 | `EwpKycDb` | Present |
| KycCaseOpeningSubscriber | Customer KYC | .NET worker | — | Present |
| DM API | Documents Management | ASP.NET Core 10 | `EwpDocumentsManagementDb` + object storage | Present |
| DocumentInvalidationSubscriber | Documents Management | .NET worker | — (invalidates through the DM API) | Present |
| Compliance API (+ in-process Outbox relay, screening worker) | Compliance | ASP.NET Core 10 | `EwpComplianceDb` | Present |
| ComplianceCaseOpeningSubscriber | Compliance | .NET worker | — | Present |
| Compliance BFF + MFE | Compliance | ASP.NET Core 10 + Next.js | — | Present |
| Screening Provider Simulator | (external system stand-in) | ASP.NET Core 10 minimal API | — | Present |
| Accounts API (+ in-process Outbox relay, account-opening worker) | Accounts | ASP.NET Core 10 | `EwpAccountsDb` | Present |
| AccountApplicationOpeningSubscriber | Accounts | .NET worker | — | Present |
| Accounts BFF + MFE | Accounts | ASP.NET Core 10 + Next.js | — | Present |
| Core Banking Simulator | (external system stand-in) | ASP.NET Core 10 minimal API | — | Present |
| Notifications API (SignalR hub, REST) | Notifications | ASP.NET Core 10 | `EwpNotificationsDb` | Present |
| NotificationsSubscriber | Notifications | .NET worker | — | Present |
| Payments API (+ saga orchestrator, step runner, in-process Outbox relay) | Payments | ASP.NET Core 10 | `EwpPaymentsDb` | Present |
| PaymentsSagaReplySubscriber | Payments | .NET worker | — | Present |
| AccountsCommandSubscriber | Accounts | .NET worker | — | Present |
| Payment Network Simulator | (external system stand-in) | ASP.NET Core 10 minimal API | — | Present |
| Payments BFF + MFE | Payments | ASP.NET Core 10 + Next.js | — | Present |

An MFE and its BFF are one deployable: the MFE is a static export served by its BFF.

### 7.2 Rules

1. **Workers belong to a context.** A relay or subscriber is deployed and versioned with the context whose database or API it uses. `src/AsyncWorkflows` is a folder, not a shared layer. EWP V3 deliberately shows two relay styles: a separate worker (Customer Onboarding) and an in-process hosted service (KYC).
2. **Shared code is technical only.** Allowed: technical libraries (`AsyncWorkflows.Infrastructure.Kafka`, `Common.WebUtilities`) and versioned integration contracts. Forbidden: domain types, DbContexts, business rules. Consumers may keep their own tolerant-reader models instead of a shared contract package (as the KYC Case Opening Subscriber does).
3. **Configuration and secrets are per deployable.** A deployable receives only its own URLs, client IDs and secrets, from its environment or a secret store.
   - **Done:** client secrets were removed from `Common.Landscape`; each deployable reads its own from configuration and refuses to start without them.
   - **Gap:** `Common.Landscape` still compiles every component's URLs and client IDs into every component, so changing one forces a rebuild of the others.
4. **Contracts evolve compatibly.** Event changes are additive within a version, and consumers tolerate unknown fields and older shapes (see the [Event Catalogue §2](Integration-Event-Catalogue.md#2-conventions)).
5. **Release checklist** for any component:
   - It builds and its tests pass in isolation.
   - It starts with only its own configuration.
   - It does not require another component to be redeployed.
   - Its database migrations are backward compatible with the previous release.
   - It can be rolled back on its own.

---

## 8. Front-End Composition

EWP V3 composes independently deployable Next.js MFEs inside a business-neutral Shell, using iframe isolation and an explicit `postMessage` protocol. Each MFE is backed by its own BFF. The menu, Application Workspace, MFE protocol, sign-in and logout are specified in [Shell-Requirements.md](../src/Shell/doc/Shell-Requirements.md).

---

## 9. Identity and Access

- **Human authentication:** Browser → BFF → IDP using Authorization Code + PKCE. Tokens stay in the BFF and never reach browser JavaScript.
- **Machine authentication:** OAuth 2.0 Client Credentials, one pinned client per caller–callee purpose.
- **Human initiator:** captured at the Outbox boundary (`initiated_by`) and carried in events. It provides accountability and determines the notification audience; it is never a grant.
- **Authorization:** RBAC + ABAC + ReBAC + workflow state + SoD; see [Authorization-Model.md](Authorization-Model.md).
- IDP clients, scopes, claims and demo data: [IDP-Requirements.md](../src/IDP/doc/IDP-Requirements.md).

---

## 10. Distributed Workflow Architecture

### 10.1 Transactional Outbox

```text
BEGIN; business state change; INSERT outbox_messages(...); COMMIT;   →  relay  →  Kafka
```

The relay publishes only after commit. Both relays (Customer Onboarding, KYC) provide:

- safe for multiple instances (`FOR UPDATE SKIP LOCKED` or partition ownership);
- bounded attempts with a parked or dead-letter state;
- per-aggregate ordering (a failed message blocks later messages of the same aggregate only);
- an idempotent producer with `acks=all`.

### 10.2 At-least-once delivery and the Inbox

Duplicates are normal: a relay can crash after publishing and before marking the row. Every consumer therefore records processed `MessageId`s:

```text
Receive ─► already in Inbox? ─yes─► ACK
                 │no
                 ▼
BEGIN; business change; next Outbox event; INSERT inbox(message_id, consumer); COMMIT ─► ACK
```

Uniqueness is `UNIQUE(message_id, consumer)`.

### 10.3 Event envelope and traceability

The envelope, the meaning of `WorkflowId` / `CorrelationId` / `CausationId` / `TraceId` / `MessageId` / `InitiatedByUserId`, and every topic are defined in [Integration-Event-Catalogue.md](Integration-Event-Catalogue.md).

### 10.4 Subscriber / worker responsibilities

A reference worker:

1. consumes one topic;
2. validates the envelope;
3. checks the Inbox;
4. extracts workflow metadata;
5. obtains and caches an M2M token;
6. calls its own context's API (or command handler);
7. applies a timeout, bounded retry with jitter, and a circuit breaker;
8. commits the business change, the next Outbox event and the Inbox row atomically;
9. acknowledges the message only after success;
10. emits structured logs and traces;
11. parks unrecoverable messages in a dead-letter topic **without stopping**.

A worker never impersonates the human initiator.

### 10.5 Sagas

Customer Onboarding uses **choreography**; Payments will use **orchestration**. Both are specified in [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md).

### 10.6 Real-time notifications

A separate Notifications component turns business events into neutral, addressed notifications and pushes them over SignalR to the right users only. The Shell renders them. Present since 4a: [Notifications API README](../src/Microservices/Notifications/API/README.md). See [Shell-Requirements §7](../src/Shell/doc/Shell-Requirements.md#7-workflow-notifications) and [Authorization-Model §11](Authorization-Model.md#11-notification-authorization).

---

## 11. Data Architecture

**Rules:**

- one database per context; no cross-database foreign keys or queries;
- integration only through APIs and events;
- versioned schema changes (migrations);
- a least-privilege database account per service;
- encrypted connections;
- tested backups and point-in-time recovery where required.

**Conventions:**

- PostgreSQL identifiers are lowercase `snake_case`; C# uses PascalCase; quoted PascalCase identifiers are avoided.
- Databases are named `Ewp<Context>Db`.
- Stored procedures are not used for ordinary CRUD.
- Optimistic concurrency uses version columns.

**Gaps:**

- Encrypted database connections (TLS) are not configured locally. (Each service already connects with its own least-privilege database user: `db/EwpServiceDbUsers.sql`.)
- Schema scripts start with `DROP TABLE`, and there are no migrations yet.

---

## 12. Security Architecture (cross-cutting targets)

Context-specific controls (document security, IDP hardening, Shell browser controls) live in the respective requirements documents. Platform-wide targets:

| Area | Targets |
|---|---|
| Data protection | Data classification; encryption in transit and at rest; key management; field-level protection where justified; PII masking in logs and telemetry; retention and secure deletion; purpose limitation. Secrets, tokens and personal data never appear in logs. |
| API security | Validation of audience, issuer, lifetime and scope (per operation); object-level authorization; input validation; request-size limits; rate limiting; secure headers; CORS restrictions; idempotency keys for commands; versioning; consistent, non-leaking errors |
| OAuth direction | Aligned with RFC 9700 and OAuth 2.1: exact redirect URIs; PKCE; no implicit or ROPC flows; refresh-token rotation; sender-constrained tokens (DPoP / mTLS) where justified; key rotation. Evaluate **FAPI 2.0** for high-risk financial APIs. |
| BFF security | HttpOnly + Secure cookies; deliberate SameSite; anti-forgery; session fixation protection and renewal; server-side session storage; logout propagation; strict origin validation; no generic proxying |
| Browser security | Strict CSP including `frame-ancestors`; `nosniff`; Referrer-Policy; Permissions-Policy; HSTS; clickjacking and open-redirect protection; `postMessage` origin and source validation |
| Input and output | Untrusted-by-default input; canonicalization; safe parsing; parameterized queries; output encoding; protection against SSRF, path traversal and deserialization attacks |
| Secrets and keys | Workload / managed identity → secrets and KMS. Rotation, versioning, expiry, access audit; separate keys per environment; no hard-coded secrets. TLS 1.2+ (prefer 1.3). |
| Service-to-service | Strong client authentication; least-privilege scopes; audience restriction; mTLS or workload identity where justified; short-lived tokens; network segmentation; explicit authorization |
| Zero trust | Never trust by network location: authenticate, authorize, validate context, least privilege, observe |
| Supply chain and DevSecOps | SAST, SCA, secret scanning, SBOM, IaC scanning, container scanning, DAST / API testing, signed artifacts and provenance, pinned dependencies — all in the pipeline, with severity and remediation SLAs |
| Infrastructure and edge | Network segmentation; private subnets; WAF / API gateway; DDoS protection; container hardening; Kubernetes RBAC and network policies; IaC with drift detection. The edge complements application authorization; it never replaces it. |
| Rate limiting | Login, token endpoints, uploads, payment initiation, expensive queries, administrative APIs. Keyed by user, client, IP, endpoint and risk. |
| Security monitoring | Authentication and authorization failures, privilege changes, credential misuse, anomalous data access, document-download and payment anomalies feed SIEM / SOC (Production Concern) |
| Fraud and risk | Risk signals → allow / challenge / hold / reject / manual review; simulated in the PoC |

---

## 13. Resilience, Messaging and Operations

| Area | Targets |
|---|---|
| Resilience | Bounded retries with exponential backoff and jitter; timeouts; circuit breakers; bulkheads; load shedding; graceful degradation; health checks with liveness / readiness separation. A retry must never turn one payment into two (idempotency keys). |
| Kafka | Explicit topics (no auto-create); keys and ordering documented per event; retry and dead-letter topics; ACLs, authentication and TLS; retention policies; consumer-lag monitoring; schema versioning |
| Observability | OpenTelemetry logs, metrics and traces; W3C trace context propagated through HTTP **and Kafka headers**; workflow duration and failure rates; circuit-breaker state; retry counts; masked sensitive data |
| Operational resilience | Operators can find a failed workflow, its initiator, its last successful step and the failed message; inspect Outbox, Inbox and saga state; replay safely; trigger controlled compensation; disable a consumer. All of this is audited. |
| Availability | HA, replication, backups, PITR, DR (RPO / RTO), multi-zone or multi-region deployment, runbooks (Production Concern; modelled in the PoC) |
| Configuration | Externalized, validated at start-up, environment-specific, secret-free in source control |

---

## 14. Testing Strategy

| Level | Scope |
|---|---|
| Unit | Aggregates and value objects, state transitions, authorization handlers, idempotency rules |
| Integration | Database, Outbox / Inbox, Kafka, IDP, downstream APIs |
| Contract | Event schemas and API contracts, producer / consumer compatibility |
| End-to-end | Login, MFE navigation, onboarding, KYC review, notifications |
| Security | Authorization categories of [Authorization-Model §15](Authorization-Model.md#15-authorization-test-categories); CSRF, XSS, SSRF, injection, token validation, session security |
| Chaos / failure | Kafka down; duplicate delivery; consumer crash; API timeout; transient and permanent HTTP failures; database failure; stale workflow state; duplicate commands; compensation failure |

**Gap:** there are no automated tests yet (`tst/` is empty).

---

## 15. Capability Matrix

| Capability | Status |
|---|---|
| Bounded contexts, database per context | Present |
| DDD tactical model | Present (aggregates, value objects, domain events and invariants in CO, KYC, Compliance, Accounts, Payments, Notifications and DM) |
| Independent deployability | Partial (secrets now per deployable; service URLs still compiled into `Common.Landscape`) |
| Next.js MFEs, Shell composition, Shell BFF, MFE BFFs (.NET and NestJS) | Present |
| Application Workspace, opaque context exchange, navigation protocol | Present |
| Duende IdentityServer 8, OIDC, Authorization Code + PKCE | Present |
| M2M Client Credentials (pinned clients) | Present |
| RBAC | Present |
| ABAC | Present (department and clearance on KYC, Compliance and Accounts actions; Compliance approval clearance by case risk; payment approval limit by clearance; branch scope in CO, KYC, Compliance, Accounts, Payments and DM; stage-specific KYC permissions) |
| ReBAC | Present (owned by the contexts: CO managing agent, KYC, Compliance and Accounts assigned officer) |
| Separation of Duties | Present across contexts (initiator excluded from KYC, Compliance and Accounts; KYC stage deciders excluded from Compliance; the Compliance approver excluded from Accounts; a payment's initiator never approves it; enforced in the aggregates, failing closed); Partial (four-eyes per KYC stage planned) |
| Workflow-state authorization | Present (each aggregate allows an action only in the right state: CO transitions, KYC stages, Compliance and Accounts decisions, a payment decided only while PENDING_APPROVAL, "Retry release" only when COMPENSATION_FAILED) |
| Object-level authorization | Present (branch scope on every read and write in CO, KYC, Compliance, Accounts, Payments and DM; payment operations read all branches but cannot decide; auditors read only through the Audit context, every look recorded) |
| Transactional Outbox with `initiated_by` | Present (CO, KYC, Compliance, Accounts and Payments) |
| Standard event envelope; Workflow / Correlation / Causation IDs | Present (CO, KYC, Compliance, Accounts and Payments, `SchemaVersion` 1; copies in Kafka headers) |
| Kafka backbone, at-least-once model | Present |
| KYC Case Opening Subscriber (M2M, bounded retry) | Present |
| Inbox / idempotent consumer | Present (CO, KYC, Compliance, Accounts, Payments, Documents Management and Notifications APIs: `inbox_messages`, written in the same transaction as the change) |
| Timeouts | Present for every worker → API call (per attempt and total budget), the calls to external systems, and every BFF → API call (the .NET BFFs per attempt and in total; the KYC BFF per request); the Payments saga times out missing replies and resends |
| Circuit breakers | Present on every subscriber (→ its own context's API) and on the Compliance API → external screening provider (failure is never a pass; cases wait in SCREENING with back-off); Accounts API → core banking (Idempotency-Key, so the POST is retried safely); Payments API → payment network; every .NET BFF → API call (GET-only retries; writes never retried). Partial: the KYC BFF (NestJS) has timeouts and GET-only retries, no breaker |
| Dead-letter / poison-message handling | Present (every subscriber, one shared consume loop: `AsyncWorkflows.Infrastructure.Subscribers`) |
| Saga choreography | Present (CO ⇄ KYC ⇄ Compliance ⇄ Accounts: submission to a COMPLETED onboarding) |
| Compensation | Present (a rejection at any stage, and a failed account opening after approval: CO COMPENSATING → REJECTED; DM invalidates and retains the evidence; the customer returns to PROSPECT) |
| Saga orchestration (Payments) | Present: persisted `PaymentSaga` state machine in the Payments API, commands / replies over Kafka with Outbox + Inbox, timeouts and resends, compensation (release funds), approval by a payments officer above the tier, COMPENSATION_FAILED as a recoverable state (Payment Processing Monitor, operations "Retry release"); assisted-channel screens |
| User-specific SignalR notifications | Present (Notifications API stores and pushes to `user:{sub}` / `staff:{role}:{branch}` audiences derived from the token; the Shell proxies REST and the hub and shows a bell and toasts; connections close at token expiry). A click opens the record through the menu-owned microservice and the normal navigation (4c). A backplane for several instances is planned |
| Centralized audit trail | Present: the Audit Domain API records every business event from Kafka in an append-only, hash-chained trail (INSERT/SELECT-only user, trigger, scheduled chain verification on readiness and metrics; identifiers and people only, no personal data). Auditors search it, follow one record end to end (with its live status from the owning context) and verify integrity in the Audit Trail screen; every read is itself recorded. Partial: operations' "Retry release" is not yet an event |
| OpenTelemetry / distributed tracing | Present (.NET components: one trace across HTTP, the Outbox and Kafka via `traceparent`; OTLP export when configured); Partial (KYC NestJS BFF not instrumented) |
| Structured logs and metrics | Present (.NET components: Serilog with service name and trace ID, one line per request, JSON outside Development; Prometheus `/metrics` with HTTP, rate limiting, auth, resilience, database and runtime metrics plus Kafka publish / consume outcomes and every health check's status and numbers; logs and metrics over OTLP when configured). Dashboards and alerts belong to the deployment |
| Security headers / CSP | Present: strict CSP on the Shell and the CO, KYC, Compliance, Accounts and Payments BFFs (hashed inline scripts, no inline styles, `frame-ancestors` / `frame-src`, `object-src 'none'`); the Audit web app by per-request nonce; IDP CSP on its pages; host filtering (`AllowedHosts`) per service; `nosniff`; Referrer-Policy |
| Cookie hardening | Present: session and anti-forgery cookies HttpOnly (session), Secure, `SameSite=Lax`; OIDC correlation / nonce cookies `None` for the login round trip only |
| Logout propagation | Present: front-channel and back-channel logout on the Shell and every MFE BFF |
| Error responses without internals | Present: problem details with `traceId` only; details logged |
| Least-privilege database users | Present: one user per service, own database only, no DDL (`db/EwpServiceDbUsers.sql`); each BFF only its own schema of `EwpBffStateDb` (`db/EwpBffStateDb.sql`) |
| Dependency vulnerability scanning | Present: `ps/build/Scan-Dependencies.ps1` (NuGet + pnpm); not yet wired into a CI pipeline |
| Rate limiting | Present: IDP login throttling and lockout; every .NET BFF and API limits per caller (person by subject ID, with a tighter budget for changes; machine client by client ID; anonymous by IP) and answers 429 with Retry-After. Shared code: `Common.WebUtilities/Security/RateLimiting.cs`, configuration `RateLimiting` |
| Health checks | Present: `/health/live` and `/health/ready` on every .NET component (workers via a built-in listener); relay heartbeat and Outbox backlog (Degraded) checks |
| Kafka authentication and authorization | Present: SCRAM-SHA-512 user per deployable, deny-by-default ACLs (own topics and consumer group only), no topic auto-creation; TLS (`SASL_SSL`) is a Production Concern |
| IDP hardening (lockout, no enumeration, POST logout, front-channel logout, refresh-token rotation) | Present; refresh tokens, PAR requests and signing keys in Duende's operational store (PostgreSQL), so an IDP restart signs nobody out |
| MFA and step-up | Present, off by default (`Mfa:Enabled`): TOTP with Google Authenticator for every user, enrolment at sign-in with recovery codes; tokens carry `amr`, and officer decisions and operations' retry require `mfa` while it is on |
| Server-side BFF sessions | Present for the .NET BFFs: Duende sessions and Data Protection keys in PostgreSQL (`EwpBffStateDb`, one schema and user per BFF), so restarts and several instances keep sessions; key rings encrypted at rest (certificate, or DPAPI in Development) or the application refuses to start; KYC (NestJS) BFF still in-memory |
| Automated tests | Planned (none yet) |
| Document content verification (allow-list + magic bytes) | Present |
| Direct / pre-signed document upload, malware scanning | Planned / Target |
| Secrets per deployable, fail closed when missing | Present |
| Secret store, key rotation | Target |
| SBOM, SAST / SCA / DAST, artifact signing | Target |
| WAF, DDoS, SIEM / SOC, HA, DR, multi-region, regulatory evidence | Production Concern |

---

## 16. Roadmap

**Phase 1 — Foundation:** event identity and workflow metadata
- [x] Transactional Outbox
- [x] Persist the human initiator
- [x] Standard event envelope (CO)
- [x] Workflow, Correlation and Causation IDs
- [x] Initiator propagation through Kafka
- [x] KYC adopts the standard envelope
- [x] TraceId in Kafka headers (`traceparent`)

**Phase 2 — Reliable subscribers:** a production-style worker
- [x] KYC Case Opening Subscriber
- [x] M2M Client Credentials
- [x] API authorization of the M2M caller
- [x] Bounded retry
- [x] Transactional business update + next Outbox event (KYC)
- [x] Inbox / idempotency (Customer Onboarding consumer)
- [x] Timeout, circuit breaker, dead-letter handling (Customer Onboarding consumer)
- [x] The same for the KYC Case Opening Subscriber (shared consume loop)
- [ ] Timeout on every call
- [x] Structured tracing (OpenTelemetry spans per message)

**Phase 3 — Distributed workflow:** a complete choreographed saga
- [x] Customer Onboarding
- [x] KYC human review
- [x] KYC triggered per application
- [x] CO reacts to KYC outcomes
- [x] Compliance (backend and officer UI)
- [x] Account opening (backend and officer UI)
- [x] Compensation on rejection (DM document invalidation)
- [ ] Failure recovery tooling (dead-letter replay; the Payments "Retry release" is present)
- [ ] Workflow audit history

**Phase 4 — Human workflow feedback**
- [x] Notifications component (Notifications API + NotificationsSubscriber, stored with Inbox)
- [x] SignalR hub
- [x] Authenticated connections (through the Shell BFF; Origin check on the WebSocket)
- [x] Audience policy (person and role-in-branch, from the token)
- [x] Completion and failure notifications (stored, pushed, shown in the Shell)

**Phase 5 — Enterprise security**
- [x] ABAC across all contexts (branch scope, department, clearance by risk)
- [x] ReBAC (managing agent in CO; assigned officer in KYC, Compliance and Accounts)
- [x] SoD that fails closed
- [x] Object-level authorization (every context)
- [x] Persistent server-side BFF sessions and Data Protection keys (.NET BFFs)
- [x] Audit trail (tamper-evident, from Kafka; auditor screens)
- [ ] PII-aware logging
- [ ] Secrets management
- [ ] Key rotation
- [x] CSP and security headers
- [x] Rate limiting
- [x] Delegated user context (token exchange, RFC 8693: Audit, and the CO and KYC BFFs → Documents Management)
- [x] MFA (TOTP, Google Authenticator) and step-up for officer decisions - off by default
- [ ] Sender-constrained tokens where justified

**Phase 6 — Secure delivery**
- [ ] Automated tests
- [ ] SAST, SCA, secret scanning
- [ ] SBOM
- [ ] Container and IaC scanning
- [ ] DAST / API testing
- [ ] Artifact signing and provenance

**Phase 7 — Operational resilience**
- [x] OpenTelemetry traces
- [x] Structured logs (Serilog) and metrics (Prometheus `/metrics`, OTLP)
- [ ] Centralized logs and metrics (a collector, Grafana / Observe - deployment)
- [ ] Kafka monitoring
- [ ] Workflow dashboards
- [ ] Alerting
- [ ] Backup / restore testing
- [ ] Chaos testing
- [ ] Runbooks
- [ ] Terraform / IaC for cloud environments

**Phase 8 — Payments orchestration**
- [x] Orchestrated saga with funds holds, timeouts and compensation
- [x] Approval by a payments officer (tier, clearance limit, separation of duties)
- [x] Assisted-channel screens, live saga timeline, notifications with deep links
- [x] Payment Processing Monitor and operations "Retry release"
- [ ] Customer self-service channel, beneficiaries

Details: [Saga plan §2](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments), [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md).

---

## 17. Definition of Architectural Success

A reviewer can trace a single business action:

```text
Human → Authentication → MFE → BFF → Authorization → Domain API → DB transaction + Outbox
      → Kafka → Subscriber (M2M identity + human initiator) → Downstream API → New state + Outbox
      → Kafka → Notification → the correct human only
```

while observing:

- no cross-service database coupling;
- no trust in browser-side authorization;
- no loss of human attribution;
- safe duplicate processing;
- bounded failure handling;
- auditable decisions;
- secure M2M communication;
- enforced Separation of Duties;
- end-to-end traceability;
- independent deployability of every component.

### Security objectives

| Objective | Question |
|---|---|
| Authentication | Can the platform reliably establish who is acting? |
| Authorization | Is this principal allowed to perform this exact operation, on this exact resource, in this exact state? |
| Accountability | Can the institution prove who did it? |
| Confidentiality | Can unauthorized parties obtain sensitive data? |
| Integrity | Can unauthorized parties change business state? |
| Availability | Can failures be isolated without corrupting workflows? |
| Auditability | Can important actions be reconstructed afterwards? |
| Resilience | Can duplicates, retries and partial failures occur without unsafe outcomes? |

Principles to preserve:

- Zero Trust
- Least Privilege
- Defense in Depth
- Secure by Default
- Fail Securely
- Verify Explicitly
- Minimize Data
- Minimize Blast Radius
- Separate Duties
- Separate Identities
- Separate Ownership
- Assume Failure
- Assume Duplicate Delivery
- Audit Important Actions
- Never Trust the UI

---

## 18. Reference Baseline

| Reference | Use |
|---|---|
| [OWASP ASVS 5.0](https://owasp.org/www-project-application-security-verification-standard/) | Verification of web-application security controls |
| [RFC 9700 — OAuth 2.0 Security BCP](https://www.rfc-editor.org/rfc/rfc9700) | Current OAuth security guidance |
| [OpenID FAPI 2.0](https://openid.net/wg/fapi/specifications/) | High-security OAuth profile for financial APIs |
| [NIST CSF 2.0](https://www.nist.gov/cyberframework) | Cybersecurity risk-management framework |
| [NIST SSDF (SP 800-218)](https://csrc.nist.gov/pubs/sp/800/218/final) | Secure software development practices |
| [RBI](https://www.rbi.org.in/) — IT Governance, Risk, Controls and Assurance; Digital Payment Security Controls | Indian banking governance and payment-security reference |

These are baselines, not claims of compliance.

---

## 19. Engineering Gaps Register

Point-in-time findings, with their remediation status, are kept in [Fellow-architect-review-of-v3-ewp.md](Fellow-architect-review-of-v3-ewp.md). Context-specific gaps are listed in the "Implementation status" section of each requirements document.

---

## 20. What This PoC Does Not Claim

EWP V3 is **not**:

- a core-banking system;
- a production payment switch;
- a production AML or fraud engine;
- a certified or RBI-compliant banking application;
- a replacement for a SOC / SIEM;
- a complete DR, regulatory or audit implementation;
- a production-grade cloud platform.

It is a technically realistic architecture in which these concerns can be demonstrated and evolved.

---

## 21. Living-Document Rule

When a capability is introduced or changed:

1. update its row in §15 and tick it in §16;
2. update the owning document — and only that one — following the [documentation map](../README.md#documentation-map);
3. record significant decisions and trade-offs;
4. keep this document about intent, principles and cross-cutting posture.

The codebase is the implementation source of truth.