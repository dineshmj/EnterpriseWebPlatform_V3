# Architectural and Security Features Demonstrable in EWP V3

A living catalogue of the architectural patterns, security controls and avoided anti-patterns that can be seen working in the current codebase. It follows current industry practice: Domain-Driven Design, event-driven microservices, OAuth 2.1 / OpenID Connect with the Backend-for-Frontend pattern, and OWASP guidance. Every entry has a short description and a **Where to look at** list that points into the solution.

The document answers *how the platform is built and protected*, not *which business functions it offers*. Where a closely related capability is still missing, the entry says so in a **Not yet** line; planned work lives in the [Blueprint roadmap](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#16-roadmap).

---

## Common questions

| Question | Answered in |
|---|---|
| What happens if a publisher or subscriber pod is killed (AKS / EKS) and another one starts? | [1.6.1](#161-pod-replacement-and-horizontal-scaling) |
| Can I run several instances of a worker at once? | [1.6.1](#161-pod-replacement-and-horizontal-scaling), [1.3.1](#131-transactional-outbox) |
| How do you handle dead-letter queues? | [1.6.2](#162-dead-letter-handling) |
| What if Kafka delivers the same message twice? | [1.3.2](#132-idempotent-consumers-inbox) |
| What if a downstream API is down for an hour? | [1.6.3](#163-timeouts-retries-and-circuit-breakers), [1.3.3](#133-reliable-subscriber-pipeline) |
| How do you avoid losing an event when the database commit succeeds but Kafka is down? | [1.3.1](#131-transactional-outbox) |
| Are events processed in order? | [1.3.5](#135-ordering-guarantees) |
| How do you trace one business transaction across services? | [1.3.4](#134-workflow-correlation-and-causation-identity) |
| How do two people editing the same record at once not overwrite each other? | [1.6.4](#164-concurrency-control) |
| Where are the access tokens kept? Can JavaScript read them? | [2.1.1](#211-oidc-authorization-code--pkce-through-a-bff), [3.3](#33-tokens-in-the-browser) |
| How do you stop a user from opening someone else's record by changing an ID (IDOR / BOLA)? | [2.2.3](#223-object-level-authorization) |
| Is authorization only role-based? | [2.2](#22-authorization-beyond-rbac) |
| How do you stop one person from both initiating and approving? | [2.2.6](#226-separation-of-duties-makerchecker) |
| How do services authenticate to each other? | [2.1.2](#212-separate-human-and-machine-identities) |
| Does signing out of one application sign the user out everywhere? | [2.1.4](#214-single-sign-out) |
| How are XSS, clickjacking and CSRF handled? | [2.3](#23-browser-and-session-security) |
| What stops a malicious file upload? | [2.4.4](#244-file-upload-security) |
| What does an attacker learn from an error response? | [2.4.1](#241-error-responses-that-leak-nothing) |
| If one service is compromised, what can it reach in the database? | [2.4.3](#243-least-privilege-database-users) |
| How are vulnerable dependencies caught? | [2.5.1](#251-dependency-vulnerability-scanning) |

---

## Table of Contents

<!-- TOC -->
- [1. Architectural features](#1-architectural-features)
  - [1.1 Microservices architecture](#11-microservices-architecture)
    - [1.1.1 Bounded contexts, each with its own database](#111-bounded-contexts-each-with-its-own-database)
    - [1.1.2 Independently deployable components](#112-independently-deployable-components)
    - [1.1.3 Context-owned asynchronous workers](#113-context-owned-asynchronous-workers)
  - [1.2 Domain-Driven Design](#12-domain-driven-design)
    - [1.2.1 Aggregates that enforce their own invariants](#121-aggregates-that-enforce-their-own-invariants)
    - [1.2.2 Value objects](#122-value-objects)
    - [1.2.3 Domain events translated into integration events](#123-domain-events-translated-into-integration-events)
    - [1.2.4 Layered APIs with separate commands and queries](#124-layered-apis-with-separate-commands-and-queries)
  - [1.3 Event-driven integration](#13-event-driven-integration)
    - [1.3.1 Transactional Outbox](#131-transactional-outbox)
    - [1.3.2 Idempotent consumers (Inbox)](#132-idempotent-consumers-inbox)
    - [1.3.3 Reliable subscriber pipeline](#133-reliable-subscriber-pipeline)
    - [1.3.4 Workflow, correlation and causation identity](#134-workflow-correlation-and-causation-identity)
    - [1.3.5 Ordering guarantees](#135-ordering-guarantees)
    - [1.3.6 Tolerant readers and contract evolution](#136-tolerant-readers-and-contract-evolution)
  - [1.4 Saga pattern](#14-saga-pattern)
    - [1.4.1 Choreography between Customer Onboarding and KYC](#141-choreography-between-customer-onboarding-and-kyc)
  - [1.5 Front-end composition](#15-front-end-composition)
    - [1.5.1 Micro-frontends hosted by a business-neutral Shell](#151-micro-frontends-hosted-by-a-business-neutral-shell)
    - [1.5.2 Shell–MFE protocol and the Application Workspace](#152-shellmfe-protocol-and-the-application-workspace)
    - [1.5.3 One design system across independent front ends](#153-one-design-system-across-independent-front-ends)
  - [1.6 Resilience and scale-out](#16-resilience-and-scale-out)
    - [1.6.1 Pod replacement and horizontal scaling](#161-pod-replacement-and-horizontal-scaling)
    - [1.6.2 Dead-letter handling](#162-dead-letter-handling)
    - [1.6.3 Timeouts, retries and circuit breakers](#163-timeouts-retries-and-circuit-breakers)
    - [1.6.4 Concurrency control](#164-concurrency-control)
    - [1.6.5 Fail-closed configuration](#165-fail-closed-configuration)
- [2. Security features](#2-security-features)
  - [2.1 Identity](#21-identity)
    - [2.1.1 OIDC Authorization Code + PKCE through a BFF](#211-oidc-authorization-code--pkce-through-a-bff)
    - [2.1.2 Separate human and machine identities](#212-separate-human-and-machine-identities)
    - [2.1.3 Identity provider hardening](#213-identity-provider-hardening)
    - [2.1.4 Single sign-out](#214-single-sign-out)
  - [2.2 Authorization beyond RBAC](#22-authorization-beyond-rbac)
    - [2.2.1 Deny by default and strict token validation](#221-deny-by-default-and-strict-token-validation)
    - [2.2.2 RBAC with per-operation scopes](#222-rbac-with-per-operation-scopes)
    - [2.2.3 Object-level authorization](#223-object-level-authorization)
    - [2.2.4 ABAC: department, clearance and stage permissions](#224-abac-department-clearance-and-stage-permissions)
    - [2.2.5 ReBAC: relationships owned by each context](#225-rebac-relationships-owned-by-each-context)
    - [2.2.6 Separation of Duties (maker–checker)](#226-separation-of-duties-makerchecker)
  - [2.3 Browser and session security](#23-browser-and-session-security)
    - [2.3.1 Strict Content-Security-Policy with script hashes](#231-strict-content-security-policy-with-script-hashes)
    - [2.3.2 Clickjacking and iframe boundaries](#232-clickjacking-and-iframe-boundaries)
    - [2.3.3 Cookies and CSRF](#233-cookies-and-csrf)
    - [2.3.4 Session management](#234-session-management)
    - [2.3.5 Open-redirect protection](#235-open-redirect-protection)
  - [2.4 API and data protection](#24-api-and-data-protection)
    - [2.4.1 Error responses that leak nothing](#241-error-responses-that-leak-nothing)
    - [2.4.2 Injection protection](#242-injection-protection)
    - [2.4.3 Least-privilege database users](#243-least-privilege-database-users)
    - [2.4.4 File upload security](#244-file-upload-security)
    - [2.4.5 Secrets per deployable](#245-secrets-per-deployable)
  - [2.5 Supply chain](#25-supply-chain)
    - [2.5.1 Dependency vulnerability scanning](#251-dependency-vulnerability-scanning)
- [3. Anti-patterns avoided](#3-anti-patterns-avoided)
  - [3.1 Distributed monolith](#31-distributed-monolith)
  - [3.2 Dual write](#32-dual-write)
  - [3.3 Tokens in the browser](#33-tokens-in-the-browser)
  - [3.4 The BFF as a saga coordinator or generic proxy](#34-the-bff-as-a-saga-coordinator-or-generic-proxy)
  - [3.5 Temporal coupling through synchronous chains](#35-temporal-coupling-through-synchronous-chains)
  - [3.6 Leaking database IDs across contexts](#36-leaking-database-ids-across-contexts)
  - [3.7 Retry storms and unsafe retries](#37-retry-storms-and-unsafe-retries)
  - [3.8 Anaemic domain model](#38-anaemic-domain-model)
  - [3.9 Trusting the UI](#39-trusting-the-ui)

---

## 1. Architectural features

### 1.1 Microservices architecture

#### 1.1.1 Bounded contexts, each with its own database

Each business capability (Customer Onboarding, Customer KYC, Documents Management) is a bounded context with its own model, language and PostgreSQL database. No context reads or writes another context's tables, and there are no cross-database foreign keys. Contexts refer to each other's records by business identifier only, for example an application's never-repeating `ApplicationRef` (UUID v7) and its `ApplicationNumber`, never by another context's database ID. Collaboration happens only through published events or explicit APIs, so each context can change its schema without coordinating with the others.

**Where to look at:**

- Database scripts, one per context: [EwpCustomerDb.sql](../src/Microservices/CustomerOnboarding/API/CustomerDB/EwpCustomerDb.sql), [EwpKycDb.sql](../src/Microservices/CustomerKyc/API/KycDb/EwpKycDb.sql), [EwpDocumentsManagementDb.sql](../src/Microservices/DocumentsManagement/API/DocumentMgmtDB/EwpDocumentsManagementDb.sql)
- Cross-context reference by business identifier: `ApplicationRef` in the [`KycCase`](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs) aggregate
- Context map: [Blueprint §5](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#5-bounded-contexts-and-context-map)

#### 1.1.2 Independently deployable components

Every API, BFF, worker and front end is its own deployable with its own configuration and secrets. A micro-frontend and its BFF ship together: the Next.js app is exported as static files and served by its BFF, so the pair can be released and rolled back as one unit without touching the Shell or other contexts. Contexts can even use different stacks: the Customer Onboarding BFF is ASP.NET Core, the Customer KYC BFF is NestJS. Both sit behind the same Shell and the same protocol.

**Where to look at:**

- .NET BFF serving its exported MFE: [CustomerOnboarding/BFF.Web](../src/Microservices/CustomerOnboarding/BFF.Web)
- NestJS BFF serving its exported MFE: [CustomerKyc/BFF.Web](../src/Microservices/CustomerKyc/BFF.Web)
- Build and export of all front ends: [CompileAndExportBFFClients_V3.ps1](../CompileAndExportBFFClients_V3.ps1)
- Rules and release checklist: [Blueprint §7](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#7-independent-deployability)

**Not yet:** service URLs and client IDs are still compiled into `Common.Landscape`, so changing one forces a rebuild of the others. Secrets are already per deployable.

#### 1.1.3 Context-owned asynchronous workers

Kafka relays and subscribers belong to the bounded context whose database or API they use, and are versioned and deployed with it. `src/AsyncWorkflows` is a folder, not a shared layer. Each worker is named after its owner and purpose: `KycCaseOpeningSubscriber` (Customer KYC) opens KYC cases from onboarding events, and `OnboardingOutcomeSubscriber` (Customer Onboarding) records KYC outcomes on applications. A worker never writes to a database directly. It calls its own context's API, so every business rule stays in one place.

**Where to look at:**

- [KycCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md), [OnboardingOutcomeSubscriber](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md)
- Internal, M2M-only endpoints the workers call: [InternalKycCasesController.cs](../src/Microservices/CustomerKyc/API/Controllers/InternalKycCasesController.cs), [InternalOnboardingApplicationsController.cs](../src/Microservices/CustomerOnboarding/API/API/Controllers/InternalOnboardingApplicationsController.cs)

### 1.2 Domain-Driven Design

#### 1.2.1 Aggregates that enforce their own invariants

Business rules live in aggregates, not in controllers, BFFs or UIs. An aggregate is changed only through its methods. Each method checks the rule, changes state and records a domain event, so an invalid state cannot be reached from outside. `KycCase.DecideStage` enforces the stage workflow, separation of duties and case assignment. `OnboardingApplication` decides which status transitions are allowed and tolerates KYC facts that arrive twice or out of order. Aggregates reference other aggregates by ID only, and take the current time as a parameter (`TimeProvider`), so they are deterministic and testable.

**Where to look at:**

- [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs)
- [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs), [Customer.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/Customer.cs)
- [Document.cs](../src/Microservices/DocumentsManagement/API/Domain/Aggregates/Document.cs)

#### 1.2.2 Value objects

Concepts such as a person's name, an e-mail address, a postal address, a branch code or a KYC verification stage are immutable value objects. They are validated when created and compared by value, so an invalid branch code or e-mail address cannot exist anywhere in the domain model. They are mapped to ordinary columns with EF Core value converters and complex types, so the database schema stays plain and readable.

**Where to look at:**

- Customer Onboarding: [ValueObjects](../src/Microservices/CustomerOnboarding/API/Domain/ValueObjects) (`PersonName`, `EmailAddress`, `PhoneNumber`, `PostalAddress`, `CustomerNumber`, `ApplicationNumber`, `BranchCode`)
- Customer KYC: [VerificationStage.cs](../src/Microservices/CustomerKyc/API/Domain/ValueObjects/VerificationStage.cs) (an EF Core complex type), [BranchCode.cs](../src/Microservices/CustomerKyc/API/Domain/ValueObjects/BranchCode.cs), [DecisionRemarks.cs](../src/Microservices/CustomerKyc/API/Domain/ValueObjects/DecisionRemarks.cs)
- Documents Management: [DocumentValueObjects.cs](../src/Microservices/DocumentsManagement/API/Domain/ValueObjects/DocumentValueObjects.cs)

#### 1.2.3 Domain events translated into integration events

Aggregates raise in-process **domain events** in their own language. A dedicated mapper in the infrastructure layer translates them into published **integration events**, the contract other contexts consume, and writes them to the Outbox. The domain model therefore never depends on Kafka or on the shape of a public contract. A domain event that matters only inside the context can stay internal and is never published.

**Where to look at:**

- [CustomerIntegrationEventMapper.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/CustomerIntegrationEventMapper.cs), [KycIntegrationEventMapper.cs](../src/Microservices/CustomerKyc/API/Infrastructure/Messaging/KycIntegrationEventMapper.cs)
- Domain events: [CustomerOnboarding/API/Domain/Events](../src/Microservices/CustomerOnboarding/API/Domain/Events), [KycDomainEvents.cs](../src/Microservices/CustomerKyc/API/Domain/Events/KycDomainEvents.cs)
- Topic contracts: [Integration-Event-Catalogue.md](Integration-Event-Catalogue.md)

#### 1.2.4 Layered APIs with separate commands and queries

Each API is split into Domain, Application, Infrastructure and API layers. The domain depends on nothing; the application layer defines abstractions (repository, unit of work, Inbox) that the infrastructure implements. Commands change one aggregate in one transaction, and queries read projections and never change state (CQRS without separate databases). Controllers stay thin: they map requests to commands and results to HTTP.

**Where to look at:**

- Command handlers: [OpenKycCaseCommandHandler.cs](../src/Microservices/CustomerKyc/API/Application/Commands/OpenKycCase/OpenKycCaseCommandHandler.cs), [SubmitOnboardingApplicationCommandHandler.cs](../src/Microservices/CustomerOnboarding/API/Application/Onboarding/Commands/SubmitApplication/SubmitOnboardingApplicationCommandHandler.cs)
- Queries: [KycCaseQueries.cs](../src/Microservices/CustomerKyc/API/Application/Queries/KycCaseQueries.cs), [CustomerOnboarding/API/Application/Customers/Queries](../src/Microservices/CustomerOnboarding/API/Application/Customers/Queries)
- Abstractions: [Persistence.cs](../src/Microservices/CustomerKyc/API/Application/Abstractions/Persistence.cs)

### 1.3 Event-driven integration

#### 1.3.1 Transactional Outbox

A business change and the event announcing it are committed in one database transaction. The event is written to an `outbox_messages` table instead of being sent to Kafka directly. A relay then publishes committed rows to Kafka. A crash can therefore never leave a change without its event, or an event without its change. The relays are safe to run as several instances (`FOR UPDATE SKIP LOCKED`). They publish each aggregate's events strictly in order, bound their retries with exponential back-off, park rows that keep failing, and use one idempotent `acks=all` producer. Customer Onboarding runs its relay as a separate worker; KYC runs it as a hosted service inside the API. The two styles are shown side by side on purpose.

**Where to look at:**

- Writing the Outbox row in the same transaction: [CustomerDbContext.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Persistence/CustomerDbContext.cs), [KycDbContext.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycDbContext.cs)
- Separate relay worker (CO): [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs)
- In-process relay (KYC): [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Idempotent producer settings: [KafkaProducer.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaProducer.cs)
- Live evidence: the `outbox_messages` table in `EwpCustomerDb` and `EwpKycDb` (`published_at`, `attempt_count`, `last_error`)

#### 1.3.2 Idempotent consumers (Inbox)

Kafka delivers at least once, so every consumer must expect duplicates: a relay can crash after publishing but before marking the row, and a consumer can crash after processing but before committing its offset. The receiving API records each consumed `MessageId` in an `inbox_messages` table, unique per message and consumer, in the **same transaction** as the business change it caused. A redelivered message is recognised and changes nothing. The KYC API is idempotent twice over: by message (Inbox) and by business key (one case per `ApplicationRef`), so even a re-published submission with a new `MessageId` returns the existing case.

**Where to look at:**

- Inbox abstraction and handlers: [IInboxStore.cs](../src/Microservices/CustomerOnboarding/API/Application/Abstractions/Persistence/IInboxStore.cs), [RecordKycOutcomeCommandHandler.cs](../src/Microservices/CustomerOnboarding/API/Application/Onboarding/Commands/RecordKycOutcome/RecordKycOutcomeCommandHandler.cs), [OpenKycCaseCommandHandler.cs](../src/Microservices/CustomerKyc/API/Application/Commands/OpenKycCase/OpenKycCaseCommandHandler.cs)
- Concurrent duplicates racing on the unique key: [InternalOnboardingApplicationsController.cs](../src/Microservices/CustomerOnboarding/API/API/Controllers/InternalOnboardingApplicationsController.cs)
- Live evidence: `inbox_messages` in `EwpCustomerDb` and `EwpKycDb`

#### 1.3.3 Reliable subscriber pipeline

Every subscriber runs on one shared consume loop. A worker supplies only a processor that parses a message, calls its API and classifies the result; the delivery behaviour is identical everywhere:

- The offset is committed only after a message is processed or dead-lettered.
- A transient failure (the API is down, a timeout, an open circuit breaker) is retried in place: the consumer seeks back to the same message with growing back-off, so the message is never skipped.
- A message that can never succeed goes to a dead-letter topic, and the worker keeps running.

The worker's own M2M token is cached until shortly before it expires, with a single refresh at a time.

**Where to look at:**

- Shared library: [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [MessageProcessing.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/MessageProcessing.cs), [CachedM2MTokenClient.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/CachedM2MTokenClient.cs)
- Worker processors: [KycCaseOpeningProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Processing/KycCaseOpeningProcessor.cs), [KycOutcomeProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Processing/KycOutcomeProcessor.cs)

#### 1.3.4 Workflow, correlation and causation identity

Every event carries a `WorkflowId` (the business process), a `CorrelationId` (the end-to-end trace), a `CausationId` (the message or command that caused it) and the `InitiatedByUserId` (the accountable human). These identifiers are copied across every hop, from Customer Onboarding to KYC and back. One onboarding can therefore be reconstructed as a causal chain across databases, from the agent's submission to the KYC officer's decision. Decisions also record `acted_by_user_id`, the human who made them. A worker carries the initiator forward but never impersonates them: the initiator is used for accountability, never as a permission.

**Where to look at:**

- Envelope: [IntegrationEventEnvelope.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/IntegrationEventEnvelope.cs)
- Propagation through a hop: `X-Workflow-Id` / `X-Correlation-Id` / `X-Causation-Id` in [KycOutcomeProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Processing/KycOutcomeProcessor.cs), read by [WorkflowContextAccessor.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/WorkflowContextAccessor.cs)
- A fully traced run: [End-to-End-Processing-Walkthrough.md](End-to-End-Processing-Walkthrough.md)
- Live evidence: the `workflow_id`, `correlation_id`, `causation_id` and `initiated_by` columns of both Outbox tables

**Not yet:** W3C `traceparent` in Kafka headers and OpenTelemetry traces. KYC events still use a flat format rather than the standard envelope.

#### 1.3.5 Ordering guarantees

Ordering is guaranteed where it matters: per aggregate. Each event is published with its aggregate's ID as the Kafka key, so all events of one case or application land on the same partition, in order. The relays publish only the oldest unpublished message of each aggregate. A message that keeps failing therefore blocks later messages of the *same* aggregate only, never the whole topic. Consumers retry in place instead of skipping ahead. Facts that legitimately arrive on different topics (such as "KYC case created" and "KYC case approved") can still arrive out of order, so the receiving aggregate applies only the transitions that are still outstanding.

**Where to look at:**

- Aggregate ID as the Kafka key, oldest-first per aggregate: [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs), [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Out-of-order tolerance: `RecordKycCaseOpened` / `RecordKycApproved` in [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs)

#### 1.3.6 Tolerant readers and contract evolution

Consumers read only the fields they need into their own message models and ignore everything else. A producer can therefore add fields without breaking anyone, and no shared contract package couples the producer's and consumer's release cycles. Event changes are additive within a version.

**Where to look at:**

- [KycOutcomeMessage.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Messages/KycOutcomeMessage.cs), the private envelope records in [KycCaseOpeningProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Processing/KycCaseOpeningProcessor.cs)
- Conventions: [Integration-Event-Catalogue.md §2](Integration-Event-Catalogue.md#2-conventions)

### 1.4 Saga pattern

#### 1.4.1 Choreography between Customer Onboarding and KYC

Customer onboarding is a long-running, choreographed saga with no central coordinator. Each context performs its own local transaction, publishes the fact through its Outbox and reacts to other contexts' facts. Submitting an application causes KYC to open a case. The case being opened moves the application to `KYC_IN_PROGRESS`. The KYC decision moves it to `KYC_COMPLETED` or `REJECTED`. Human review is a persisted state, not a waiting process: nothing stays in memory while an officer is away for days. The officer's decision is a new transaction that resumes the workflow. Each context changes only the state it owns.

**Where to look at:**

- Design and rules: [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md)
- Forward hop: [KycCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md); return hop: [OnboardingOutcomeSubscriber](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md)
- Live evidence: the CO and KYC Outbox tables of one onboarding, linked by `causation_id`

**Not yet:** the Compliance and Accounts participants, compensation (e.g. invalidating documents on rejection), and the orchestrated Payments saga.

### 1.5 Front-end composition

#### 1.5.1 Micro-frontends hosted by a business-neutral Shell

The Shell provides branding, sign-in, the menu and the Application Workspace, and hosts each context's micro-frontend in an iframe. It holds no business logic: the menu comes from the Shell's database, so adding a context adds menu rows, not Shell code. The iframe gives each MFE its own origin, cookies and security boundary, so one MFE cannot read another's DOM or session.

**Where to look at:**

- Shell host page: [page.tsx](../src/Shell/client-app/app/page.tsx); menu: [MenuController.cs](../src/Shell/Controllers/MenuController.cs), [EwpBssShellDb.sql](../src/Shell/MenuDB/EwpBssShellDb.sql)
- Requirements: [Shell-Requirements.md](../src/Shell/doc/Shell-Requirements.md)

#### 1.5.2 Shell–MFE protocol and the Application Workspace

The Shell and the MFEs talk through an explicit `postMessage` protocol: ready, context hand-over, context update, and navigation request/response. The protocol lets an MFE with unsaved changes ask the user before the Shell navigates away. Whenever the user picks a record, the MFE publishes it, and the Shell's Application Workspace shows it above the iframe and hands it to the next MFE. To the Shell the context is opaque: it never interprets business data, and a receiving MFE re-reads anything authoritative from its own BFF. Both sides check the sender's origin and window and post only to an explicit origin; the MFE's trusted parent comes from a static allow-list, never from `document.referrer`.

**Where to look at:**

- MFE side: [MfeShell.tsx (KYC)](../src/Microservices/CustomerKyc/BFF.Web/client-app/app/components/MfeShell.tsx), [MfeShell.tsx (CO)](../src/Microservices/CustomerOnboarding/BFF.Web/client-app/app/components/MfeShell.tsx)
- Shell side: [page.tsx](../src/Shell/client-app/app/page.tsx), [ApplicationWorkspace.tsx](../src/Shell/client-app/app/components/ApplicationWorkspace.tsx)
- Protocol and selection rules: [Shell-Requirements.md §3–4](../src/Shell/doc/Shell-Requirements.md#3-application-workspace)

#### 1.5.3 One design system across independent front ends

All front ends share one set of design tokens (colours, typography, radii, shadows) as plain CSS variables. The MFEs use them through Tailwind CSS v4, and the IDP's Razor pages use them directly. The IDP copies the tokens at build time, so every front end stays independently deployable while looking like one product. Styles are compiled or served as files, with no CSS-in-JS and no CDN, which keeps the strict Content-Security-Policy intact.

**Where to look at:**

- Tokens and Tailwind mapping: [src/Common/DesignSystem](../src/Common/DesignSystem/README.md)
- Copy at build time: the `CopyDesignSystemAssets` target in [EnterpriseWebPlatform.IdentityServer.csproj](../src/IDP/EnterpriseWebPlatform.IdentityServer.csproj)

### 1.6 Resilience and scale-out

#### 1.6.1 Pod replacement and horizontal scaling

The asynchronous components assume that any instance can be killed at any moment (an AKS or EKS eviction, a rolling deployment, a node failure) and that several instances may run at once:

| Moment of failure | What happens |
|---|---|
| Relay killed after claiming Outbox rows, before publishing | The database releases the row locks when the connection drops; another instance (or the restarted one) claims the rows. |
| Relay killed after publishing, before marking the row | The row is published again. The consumer's Inbox recognises the duplicate. |
| Subscriber killed while processing | The offset was not committed, so Kafka redelivers the message to whichever instance owns the partition next. The API's Inbox makes the repeat harmless. |
| Two relay instances running | `FOR UPDATE SKIP LOCKED` gives each row to exactly one instance. |
| Two subscriber instances running | They share one consumer group; Kafka assigns each partition to one instance, and rebalances when an instance leaves. |
| Graceful shutdown (SIGTERM) | The subscriber leaves the consumer group cleanly, so its partitions are reassigned at once; the producer flushes pending messages. |

No step relies on in-memory state surviving a restart: workflow state lives in the databases and in Kafka offsets.

**Where to look at:**

- Claiming rows: the `FOR UPDATE SKIP LOCKED` queries in [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs) and [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Commit after processing, clean group exit: [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)
- Flush on shutdown: [KafkaProducer.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaProducer.cs)

**Not yet:** liveness and readiness endpoints for the orchestrator's probes. The BFFs keep server-side sessions in memory, so scaling a BFF beyond one instance needs a shared session store or sticky sessions.

#### 1.6.2 Dead-letter handling

Two kinds of failure are told apart. **Transient** failures (a dependency is down) are retried in place and never dead-lettered. **Permanent** failures (malformed JSON, an unknown event type, missing required fields, a 4xx rejection from the API) can never succeed. Such a message is copied unchanged to the worker's dead-letter topic, with headers recording the reason, the original topic, partition and offset, the consumer group and the time; then it is committed, so the partition keeps flowing. If writing to the dead-letter topic itself fails, the message is retried rather than lost. On the publishing side, an Outbox row that fails ten times is **parked** (it stays unpublished with `attempt_count` and `last_error` for an operator) instead of being retried forever.

**Where to look at:**

- Dead-letter step: `DeadLetterAsync` in [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)
- Topics `customer-kyc.case-opening-subscriber.dlq` and `customer-onboarding.kyc-subscriber.dlq`: [KafkaTopicNames.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaTopicNames.cs), [Integration-Event-Catalogue.md §4.3](Integration-Event-Catalogue.md#43-dead-letter-topics)
- Parked Outbox rows: `MaxAttempts` in [CustomerOutboxPublisherOptions.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Configuration/CustomerOutboxPublisherOptions.cs)

**Not yet:** a replay tool for dead-lettered messages, and alerting on dead-letter topic growth.

#### 1.6.3 Timeouts, retries and circuit breakers

Every call from a worker to an API runs through a resilience pipeline: a 10-second timeout per attempt, three retries with exponential back-off and jitter, a circuit breaker and a 60-second total budget. When half of the recent calls fail, the circuit opens for 30 seconds, so a struggling API gets room to recover instead of being hammered by every retry. Retrying a POST is safe here only because the endpoints are idempotent (Inbox). Where an endpoint is *not* idempotent, it is deliberately not retried: the Customer Onboarding BFF retries only GET requests.

**Where to look at:**

- Pipelines (Microsoft.Extensions.Http.Resilience / Polly): `AddStandardResilienceHandler` in the [KycCaseOpeningSubscriber Program.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Program.cs) and [OnboardingOutcomeSubscriber Program.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Program.cs)
- GET-only retries: [TransientGetRetryHandler.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Services/TransientGetRetryHandler.cs)

**Not yet:** circuit breakers on the BFF-to-API calls.

#### 1.6.4 Concurrency control

Two users acting on the same record at the same moment cannot overwrite each other. Every aggregate carries a `version`; an update based on a stale version is rejected (optimistic concurrency) and reported as a conflict. KYC decisions add a row lock (`SELECT … FOR UPDATE`) for the duration of the transaction. Two officers deciding the two stages of one case at the same moment are serialised, so each sees the other's committed outcome and the case reaches exactly one final state. Unique constraints settle races between duplicate deliveries.

**Where to look at:**

- Version columns and conflict handling: `Version` in [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs) and [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs); `SaveTranslatingErrorsAsync` in [KycDbContext.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycDbContext.cs)
- Row lock for decisions: `GetForDecisionAsync` in [KycCaseRepository.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycCaseRepository.cs)

#### 1.6.5 Fail-closed configuration

A component that is missing a required secret or setting refuses to start, rather than running with a default. Client secrets have no compiled-in values; options are validated at start-up; the KYC API refuses to start without its audience. The IDP uses its development signing key only in the Development environment. A misconfiguration shows up immediately at deployment, not as a silent security gap in production.

**Where to look at:**

- `ValidateOnStart` with secret checks: [SubscriberServiceCollectionExtensions.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs)
- Required audience: [CustomerKyc API Program.cs](../src/Microservices/CustomerKyc/API/Program.cs)
- Development-only signing key: [SigningCredentialExtensions.cs](../src/IDP/Security/SigningCredentialExtensions.cs)

---

## 2. Security features

### 2.1 Identity

#### 2.1.1 OIDC Authorization Code + PKCE through a BFF

Users sign in with OpenID Connect Authorization Code flow plus PKCE, the flow recommended by OAuth 2.1 and RFC 9700. The flow is run by each front end's **Backend-for-Frontend**, a confidential server-side client. Access and refresh tokens stay in the BFF's server-side session and never reach the browser. The browser holds only an HttpOnly session cookie that JavaScript cannot read, so a cross-site scripting bug cannot steal a token. Each MFE signs in silently against the IDP's single sign-on session (`prompt=none`), so the user signs in once for every workspace. Refresh tokens are one-time use (rotation).

**Where to look at:**

- IDP clients: [MfeCustomerKyc.cs](../src/IDP/ConfigRegistration/Clients/MFEs/MfeCustomerKyc.cs) (`RequirePkce`, `RefreshTokenUsage.OneTimeOnly`), [BssShell.cs](../src/IDP/ConfigRegistration/Clients/BssShell.cs)
- .NET BFFs with Duende BFF and server-side sessions: [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs), [Shell Program.cs](../src/Shell/Program.cs)
- NestJS BFF: [oidc.service.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/oidc.service.ts)

#### 2.1.2 Separate human and machine identities

Humans and services authenticate differently. Workers and BFFs use OAuth 2.0 Client Credentials with **one client per caller–callee purpose** (for example "KYC Case Opening Subscriber → Customer KYC API"), each with only the scope it needs. The receiving API pins the exact `client_id` on its internal endpoints, so a valid token from any *other* service is rejected even if it carries the right scope. A machine identity never acts as a human: the human initiator travels as data, for accountability only.

**Where to look at:**

- M2M clients: [IDP ConfigRegistration/Clients/M2M](../src/IDP/ConfigRegistration/Clients/M2M)
- Pinned-client policies `KycCaseOpeningSubscriberWrite` and `OnboardingOutcomeSubscriberWrite`: [CustomerKyc API Program.cs](../src/Microservices/CustomerKyc/API/Program.cs), [CustomerOnboarding API Program.cs](../src/Microservices/CustomerOnboarding/API/Program.cs)
- Client and scope inventory: [IDP-Requirements.md](../src/IDP/doc/IDP-Requirements.md)

**Not yet:** delegated user context (RFC 8693 token exchange). Documents Management receives the acting user's branch as an asserted header from a pinned BFF client.

#### 2.1.3 Identity provider hardening

The IDP (Duende IdentityServer 8) applies the standard defences of a sign-in service:

- **Brute force:** per-account lockout (15 minutes) and per-IP login throttling.
- **Username enumeration:** an unknown username costs the same time as a wrong password (a dummy hash is verified), and both produce the same message.
- **Grants:** no implicit and no password (ROPC) grant.
- **Logout:** POST only, so it cannot be triggered cross-site.
- **Pages:** framing is forbidden (`frame-ancestors 'none'`); the pages use no CDN and no inline script or style, so their CSP has no `'unsafe-inline'`.
- **Passwords:** password managers are supported (`autocomplete="current-password"`).

**Where to look at:**

- Lockout and dummy hash: [UserRepository.cs](../src/IDP/Repositories/UserRepository.cs), [PasswordManager.cs](../src/IDP/Security/PasswordManager.cs)
- Throttling: `AddRateLimiter` in [IDP Program.cs](../src/IDP/Program.cs)
- Headers and CSP: [SecurityHeadersAttribute.cs](../src/IDP/SecurityHeadersAttribute.cs)

**Not yet:** multi-factor authentication.

#### 2.1.4 Single sign-out

Signing out ends the session everywhere. The IDP notifies every client: through the browser (front-channel, a hidden iframe on the signed-out page) and server-to-server (back-channel, a signed logout token posted to each BFF). Back-channel logout works even when the browser blocks third-party iframes. The KYC BFF also revokes its refresh token at logout, so the token cannot be used after the session ends.

**Where to look at:**

- Front-channel iframe: [LoggedOut.cshtml](../src/IDP/Pages/Account/LoggedOut.cshtml), [signout-redirect.js](../src/IDP/wwwroot/js/signout-redirect.js)
- Back-channel endpoints: Duende BFF `/bff/backchannel` (.NET BFFs), [backchannel-logout.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/backchannel-logout.controller.ts) with [session-registry.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/session-registry.ts) (NestJS)
- Client registration (`BackChannelLogoutUri`): [BssShell.cs](../src/IDP/ConfigRegistration/Clients/BssShell.cs)

### 2.2 Authorization beyond RBAC

#### 2.2.1 Deny by default and strict token validation

APIs reject anything not explicitly allowed. Every API validates the token's issuer, audience, lifetime and signature against the IDP's published keys, and keeps the original claim names (no silent claim remapping). Customer Onboarding and Documents Management require an authorization policy on every controller by default; the KYC API puts a policy on every controller and action. The UI may hide a button, but it is never what protects an operation.

**Where to look at:**

- `MapControllers().RequireAuthorization(...)` and `MapInboundClaims = false`: [CustomerOnboarding API Program.cs](../src/Microservices/CustomerOnboarding/API/Program.cs), [DocumentsManagement API Program.cs](../src/Microservices/DocumentsManagement/API/Program.cs)
- Per-action policies: [KycCasesController.cs](../src/Microservices/CustomerKyc/API/Controllers/KycCasesController.cs)

#### 2.2.2 RBAC with per-operation scopes

The coarse layer: the calling application must hold the OAuth scope for *this kind* of operation (read vs write), and the user must hold a suitable role. A read-only token cannot call a write endpoint even if the user's role would allow it.

**Where to look at:**

- Policies `CustomerRead` / `CustomerWrite` / `OnboardingRead` / `OnboardingWrite`: [CustomerOnboarding API Program.cs](../src/Microservices/CustomerOnboarding/API/Program.cs)
- `DocumentRead` / `DocumentWrite`: [DocumentsManagement API Program.cs](../src/Microservices/DocumentsManagement/API/Program.cs)

#### 2.2.3 Object-level authorization

Having the right role is not enough to open a *specific* record. Every read, list and write checks that the record lies within the caller's scope, so changing an ID in a URL returns nothing (the defence against OWASP API Security's #1 risk, BOLA / IDOR). A customer service agent sees only customers whose primary residential address is in their branch's city. A KYC officer sees and decides only cases of their own branch. Documents are branch-scoped on every operation. Lists are filtered and paged **in the database**, so out-of-scope rows are never even loaded. A caller without the needed attributes sees nothing (fail closed).

**Where to look at:**

- [CustomerAccessScope.cs](../src/Microservices/CustomerOnboarding/API/Application/Abstractions/Authorization/CustomerAccessScope.cs), [CustomerResourceAuthorization.cs](../src/Microservices/CustomerOnboarding/API/Authorization/CustomerResourceAuthorization.cs)
- [DocumentResourceAuthorization.cs](../src/Microservices/DocumentsManagement/API/Authorization/DocumentResourceAuthorization.cs)
- KYC branch scope: [KycCaseDecisionAuthorization.cs](../src/Microservices/CustomerKyc/API/Authorization/KycCaseDecisionAuthorization.cs), [KycCaseQueries.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycCaseQueries.cs)

#### 2.2.4 ABAC: department, clearance and stage permissions

Attributes of the user, not only their role, decide what they may do. To decide a KYC stage, an officer needs the `kyc_officer` role, the KYC department, clearance level 3 or above, the decision permission (approve or reject) **and** the permission for that stage (`kyc.identity.verify` or `kyc.document.verify`). Different officers can therefore be entitled to different steps of the same case.

**Where to look at:**

- Requirement and handler: [KycCaseDecisionAuthorization.cs](../src/Microservices/CustomerKyc/API/Authorization/KycCaseDecisionAuthorization.cs)
- Stage policies `KycIdentityApprove`, `KycDocumentReject`, …: [CustomerKyc API Program.cs](../src/Microservices/CustomerKyc/API/Program.cs)
- Claims issued by the IDP: [CustomProfileService.cs](../src/IDP/Services/CustomProfileService.cs)

#### 2.2.5 ReBAC: relationships owned by each context

Some decisions depend on the relationship between a user and a specific record, not on the user's role alone. These relationships are stored and enforced by the context that owns the record, not by the IDP. In Customer Onboarding, each customer has a **managing agent**: only that agent may update the customer and open or submit applications for them. In Customer KYC, each case has an **assigned officer**. The first decision, or an explicit claim, assigns the case; only the assignee may decide it, and the assignee may release it. The rule is enforced in the API and the aggregate, so hiding a button in the UI is never what protects the record.

**Where to look at:**

- CO managing agent: [Customer.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/Customer.cs), [CustomerResourceAuthorization.cs](../src/Microservices/CustomerOnboarding/API/Authorization/CustomerResourceAuthorization.cs)
- KYC assigned officer: [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs), [AssignKycCaseCommandHandler.cs](../src/Microservices/CustomerKyc/API/Application/Commands/AssignKycCase/AssignKycCaseCommandHandler.cs)
- Claim / release endpoints (policy `KycCaseAssign`): [KycCasesController.cs](../src/Microservices/CustomerKyc/API/Controllers/KycCasesController.cs)
- Model and rules: [Authorization-Model.md](Authorization-Model.md)

#### 2.2.6 Separation of Duties (maker–checker)

The person who starts a workflow can never approve it. The agent who submits an onboarding application can neither take nor decide its KYC case. The rule lives inside the `KycCase` aggregate, so no code path can bypass it. It also fails closed: if the initiator is unknown, every decision is denied rather than allowed.

**Where to look at:**

- `EnsureSeparationOfDuties` in [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs)
- The initiator captured at the source and carried through Kafka: `initiated_by` in the Outbox tables, see [1.3.4](#134-workflow-correlation-and-causation-identity)

**Not yet:** four-eyes per stage (a different officer for each stage).

### 2.3 Browser and session security

#### 2.3.1 Strict Content-Security-Policy with script hashes

The Shell, both MFE BFFs and the IDP send a strict Content-Security-Policy. Scripts may load only from the application's own origin. The few inline scripts that Next.js's static export needs are allowed **by their SHA-256 hash**, which each BFF computes at start-up from the exported HTML, so no `'unsafe-inline'` is needed for scripts. Plugins are disabled (`object-src 'none'`), forms may post only to the same origin, and frame sources are allow-listed. A report-only switch lets a new policy be observed before it is enforced.

**Where to look at:**

- .NET builder with hash computation: [ContentSecurityPolicy.cs](../src/Common/WebUtilities/Security/ContentSecurityPolicy.cs), used by [Shell Program.cs](../src/Shell/Program.cs) and [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs)
- NestJS twin: [content-security-policy.ts](../src/Microservices/CustomerKyc/BFF.Web/src/security/content-security-policy.ts)
- Report-only switches: `Security:CspReportOnly` / `KYC_BFF_CSP_REPORT_ONLY` ([ReadMe.txt](../ReadMe.txt))

**Not yet:** the MFEs still allow inline *styles* (`style-src 'unsafe-inline'`). The IDP does not.

#### 2.3.2 Clickjacking and iframe boundaries

Every page states who may frame it. The Shell and the IDP's sign-in pages cannot be framed at all; an MFE may be framed only by the Shell (and by the IDP during its silent sign-in). A KYC evidence PDF may be framed only by its own MFE. Combined with the `postMessage` origin checks ([1.5.2](#152-shellmfe-protocol-and-the-application-workspace)), a hostile page can neither overlay a real screen to trick a click nor pose as the Shell to receive workspace context.

**Where to look at:**

- `frameAncestors` / `frameSources` in [Shell Program.cs](../src/Shell/Program.cs), [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs) and [main.ts](../src/Microservices/CustomerKyc/BFF.Web/src/main.ts)
- `X-Frame-Options: DENY` and `frame-ancestors 'none'`: [SecurityHeadersAttribute.cs](../src/IDP/SecurityHeadersAttribute.cs)

#### 2.3.3 Cookies and CSRF

Session cookies are HttpOnly, Secure, `SameSite=Lax` and use the `__Host-` prefix, which locks a cookie to one host over HTTPS. Only the short-lived OIDC correlation and nonce cookies are `SameSite=None`, and only for the sign-in round trip. Every state-changing request also needs an anti-forgery token sent in a header. The NestJS BFF compares that token in constant time. `SameSite=Lax` and the token are two independent CSRF defences.

**Where to look at:**

- [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs) (`AddAntiforgery`, cookie options), [Shell Program.cs](../src/Shell/Program.cs)
- [main.ts](../src/Microservices/CustomerKyc/BFF.Web/src/main.ts), [csrf.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/csrf.ts), [auth.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/auth.controller.ts)
- Cookie names: [CookieNames.cs](../src/Common/Landscape/CookieNames.cs)

#### 2.3.4 Session management

Sessions are stored on the server; the cookie holds only a reference to them. A session expires after 30 minutes of inactivity in the Shell and the MFE BFFs. The KYC BFF issues a fresh session ID at sign-in (protection against session fixation), and back-channel logout ([2.1.4](#214-single-sign-out)) can end a session from the server side.

**Where to look at:**

- `AddServerSideSessions` and `ExpireTimeSpan`: [Shell Program.cs](../src/Shell/Program.cs), [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs)
- `session.regenerate` at sign-in: [oidc.service.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/oidc.service.ts)

**Not yet:** session stores are in-memory (see [1.6.1](#161-pod-replacement-and-horizontal-scaling)).

#### 2.3.5 Open-redirect protection

After sign-in, a BFF sends the user back only to a route on an allow-list, never to an arbitrary `returnUrl`, so a crafted link cannot bounce a signed-in user to a phishing site.

**Where to look at:**

- [BffRouteCatalog.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Controllers/AuthController.cs)
- `safeReturnUrl` in [auth.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/auth.controller.ts)

### 2.4 API and data protection

#### 2.4.1 Error responses that leak nothing

An unexpected error returns a standard problem response with a `traceId` and nothing else: no exception message, stack trace or SQL. The details are logged on the server under the same `traceId`, so support can find them. Only the service's own domain errors (for example "the case is assigned to another officer") carry a message, because those are meant for the user. There is no developer exception page.

**Where to look at:**

- [ApiExceptionHandler.cs (KYC)](../src/Microservices/CustomerKyc/API/Controllers/ApiExceptionHandler.cs), [ApiExceptionHandler.cs (CO)](../src/Microservices/CustomerOnboarding/API/API/ErrorHandling/ApiExceptionHandler.cs), [ApiExceptionHandler.cs (DM)](../src/Microservices/DocumentsManagement/API/API/ErrorHandling/ApiExceptionHandler.cs)

#### 2.4.2 Injection protection

All database access goes through EF Core with parameters. Raw SQL, where it is used (row locks, Outbox claiming, sequences), uses interpolated `FormattableString` queries that EF Core turns into parameters, never string concatenation. Input is validated by value objects before it reaches persistence. Output in the React and Razor front ends is encoded by the frameworks; no `innerHTML`-style rendering is used.

**Where to look at:**

- Parameterised raw SQL: [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs), [ApplicationNumberGenerator.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Persistence/ApplicationNumberGenerator.cs)
- Validation at the edge of the domain: [1.2.2](#122-value-objects)

#### 2.4.3 Least-privilege database users

Each service connects with its own database user, which can reach only its own database and cannot change the schema (no DDL). The Customer Onboarding Outbox relay may only read and update the Outbox table: it cannot read customers. A compromised service, or a leaked connection string, therefore exposes one context's data at most, never the whole platform.

**Where to look at:**

- [EwpServiceDbUsers.sql](../db/EwpServiceDbUsers.sql) (roles `ewp_customer_onboarding_api`, `ewp_customer_outbox_relay`, `ewp_kyc_api`, `ewp_documents_api`, `ewp_idp`, `ewp_shell`)

#### 2.4.4 File upload security

An uploaded document is trusted for nothing it declares about itself. The content type is decided by the file's signature (magic bytes), against an allow-list of PDF, PNG and JPEG. The verified type, not the uploader's claim, is stored and served. Files are served with `X-Content-Type-Options: nosniff`; only a verified PDF is shown inline, and anything else is a download. Storage paths are resolved and checked to stay under the storage root, so a crafted file name cannot escape it (path traversal).

**Where to look at:**

- Allow-list and magic bytes: [DocumentContentPolicy.cs](../src/Microservices/DocumentsManagement/API/Domain/Policies/DocumentContentPolicy.cs)
- Inline vs attachment, `nosniff`: [kyc-cases.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/controllers/kyc-cases.controller.ts)
- Path traversal guard: [LocalFileSystemDocumentStorage.cs](../src/Microservices/DocumentsManagement/API/Infrastructure/Storage/LocalFileSystemDocumentStorage.cs)

**Not yet:** malware scanning, and encryption at rest of stored documents.

#### 2.4.5 Secrets per deployable

Each deployable receives only its own secrets, from its own configuration, and refuses to start without them ([1.6.5](#165-fail-closed-configuration)). No secret is compiled into shared code. Development values live in Development-only settings files, and outside Development they come from the environment or a secret store.

**Where to look at:**

- Per-worker secret, no default: `ClientSecret` in [KycCaseOpeningSubscriberOptions.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Configuration/KycCaseOpeningSubscriberOptions.cs)
- IDP client secrets from configuration: [ClientSecretStore.cs](../src/IDP/Security/ClientSecretStore.cs)

**Not yet:** an actual secret store (e.g. Azure Key Vault / AWS Secrets Manager) and key rotation.

### 2.5 Supply chain

#### 2.5.1 Dependency vulnerability scanning

One script checks every .NET and npm dependency of the solution against published vulnerability advisories. It fails the run when a deployed dependency has a known vulnerability at or above a chosen severity, so it can gate a CI pipeline. Dependencies are kept on patched versions (for example Next.js 16.3.8 after a critical advisory).

**Where to look at:**

- [Scan-Dependencies.ps1](../Scan-Dependencies.ps1)

**Not yet:** wiring into a CI pipeline, SAST, secret scanning, SBOM and artifact signing.

---

## 3. Anti-patterns avoided

### 3.1 Distributed monolith

**The anti-pattern:** services that are deployed separately but share a database, shared domain classes or a release train. Any change then ripples across all of them.

**How EWP V3 avoids it:** every context owns its database, and each service connects with its own database user, which cannot even reach another context's database. Shared code is technical only: Kafka plumbing, the subscriber pipeline and web utilities. There are no shared domain types or DbContexts. Consumers read events as tolerant readers with their own models, so a producer can add fields without breaking them.

**Where to look at:**

- Per-service database users: [EwpServiceDbUsers.sql](../db/EwpServiceDbUsers.sql)
- Technical-only shared libraries: [src/AsyncWorkflows/Infrastructure](../src/AsyncWorkflows/Infrastructure), [src/Common/WebUtilities](../src/Common/WebUtilities)
- Tolerant-reader message models: [1.3.6](#136-tolerant-readers-and-contract-evolution)
- Rules: [Blueprint §7.2](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#72-rules)

### 3.2 Dual write

**The anti-pattern:** saving to the database and then publishing to the message broker as two separate steps. If the process dies between them, or the broker is down, the system silently disagrees with itself.

**How EWP V3 avoids it:** the event is part of the database transaction (Transactional Outbox, [1.3.1](#131-transactional-outbox)), and the consumer's Inbox row is part of the consumer's transaction ([1.3.2](#132-idempotent-consumers-inbox)). No component writes to the database and Kafka as two independent steps.

**Where to look at:**

- [KycDbContext.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycDbContext.cs) (`SaveChangesAsync` writes the aggregate and its Outbox rows in the open transaction)

### 3.3 Tokens in the browser

**The anti-pattern:** a single-page app that holds access or refresh tokens in JavaScript (`localStorage`, `sessionStorage` or memory), where any XSS bug can steal them.

**How EWP V3 avoids it:** the BFF pattern ([2.1.1](#211-oidc-authorization-code--pkce-through-a-bff)). The browser holds only an HttpOnly session cookie; tokens stay on the server, and the BFF attaches them to API calls.

**Where to look at:**

- The MFEs' API helpers, which send cookies and a CSRF header but no `Authorization` header: [api.ts (KYC)](../src/Microservices/CustomerKyc/BFF.Web/client-app/app/lib/api.ts), [api.ts (CO)](../src/Microservices/CustomerOnboarding/BFF.Web/client-app/app/lib/api.ts)

### 3.4 The BFF as a saga coordinator or generic proxy

**The anti-pattern:** a BFF that grows into a business orchestrator (running multi-step workflows and compensations in a web request) or into an open proxy that forwards any path to any API.

**How EWP V3 avoids it:** a BFF handles one user request at a time and exposes only the routes its MFE needs. Long-running workflow steps happen in the owning contexts through events, and compensation belongs to each context, never to a BFF. The one multi-step request, uploading evidence and then creating the application, only cleans up its own uploads if that same request fails.

**Where to look at:**

- Explicit, narrow routes: [OnboardingController.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Controllers/OnboardingController.cs), [kyc-cases.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/controllers/kyc-cases.controller.ts)
- The rule: [Saga plan §1.3](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#13-the-mfe--bff-boundary)

### 3.5 Temporal coupling through synchronous chains

**The anti-pattern:** service A calls B, which calls C, inside one user request. Every service must be up at the same moment, and the slowest one sets the response time.

**How EWP V3 avoids it:** contexts collaborate asynchronously. Submitting an application commits locally and returns; KYC opens its case when it receives the event, even if KYC was down at the time of submission. Each hop is retried independently ([1.3.3](#133-reliable-subscriber-pipeline)).

**Where to look at:**

- [End-to-End-Processing-Walkthrough.md](End-to-End-Processing-Walkthrough.md)

### 3.6 Leaking database IDs across contexts

**The anti-pattern:** one service storing another service's auto-increment IDs. When that database is recreated or migrated, the IDs repeat and point to the wrong records.

**How EWP V3 avoids it:** contexts reference applications by a never-repeating `ApplicationRef` (UUID v7) plus a human-readable `ApplicationNumber` issued by the owning API from a database sequence. A consumer checks that the two still match before it acts.

**Where to look at:**

- [ApplicationNumberGenerator.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Persistence/ApplicationNumberGenerator.cs)
- The number check: `ApplicationMismatch` in [RecordKycOutcomeCommandHandler.cs](../src/Microservices/CustomerOnboarding/API/Application/Onboarding/Commands/RecordKycOutcome/RecordKycOutcomeCommandHandler.cs)

### 3.7 Retry storms and unsafe retries

**The anti-pattern:** retrying every failure immediately and forever, which turns a struggling dependency into a dead one, or retrying a non-idempotent call and doing the work twice.

**How EWP V3 avoids it:** retries are bounded, back off exponentially with jitter, and stop at an open circuit breaker ([1.6.3](#163-timeouts-retries-and-circuit-breakers)). Calls are retried only where the receiver is idempotent ([1.3.2](#132-idempotent-consumers-inbox)); the CO BFF retries GETs only.

**Where to look at:**

- [TransientGetRetryHandler.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Services/TransientGetRetryHandler.cs), [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)

### 3.8 Anaemic domain model

**The anti-pattern:** entities that are bags of public setters, with the business rules scattered across services and controllers, where any caller can put an object into an invalid state.

**How EWP V3 avoids it:** aggregates expose behaviour, not setters ([1.2.1](#121-aggregates-that-enforce-their-own-invariants)); state changes only through methods that check the rules and raise domain events.

**Where to look at:**

- `private set` properties and behaviour methods in [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs) and [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs)

### 3.9 Trusting the UI

**The anti-pattern:** relying on hidden buttons, disabled fields or client-side checks for security.

**How EWP V3 avoids it:** every rule is enforced server-side: in the API's policies, the object-level checks and the aggregate. The MFEs only mirror the outcome for usability, for example by telling an officer who a case is assigned to; a forged request is still rejected by the API.

**Where to look at:**

- [2.2 Authorization beyond RBAC](#22-authorization-beyond-rbac)
