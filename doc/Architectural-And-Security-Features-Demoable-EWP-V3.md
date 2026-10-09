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
| What happens to the uploaded documents when an application is rejected? Are they deleted? | [1.4.2](#142-compensation-on-rejection-retain-dont-delete) |
| Can an agent onboard the same customer twice, or change a verified name afterwards? | [1.2.1](#121-aggregates-that-enforce-their-own-invariants) |
| What if a downstream API is down for an hour? | [1.6.3](#163-timeouts-retries-and-circuit-breakers), [1.3.3](#133-reliable-subscriber-pipeline) |
| What if an external provider (e.g. sanctions screening) is slow or down? Does a case slip through? | [1.6.3](#163-timeouts-retries-and-circuit-breakers) |
| How do you avoid losing an event when the database commit succeeds but Kafka is down? | [1.3.1](#131-transactional-outbox) |
| What if a payment fails halfway, after the money was reserved? Can the money be lost? | [1.4.3](#143-orchestration-the-payments-saga) |
| Can a double-click or a retried request send a payment twice? | [1.4.3](#143-orchestration-the-payments-saga) |
| Are events processed in order? | [1.3.5](#135-ordering-guarantees) |
| How do you trace one business transaction across services? | [1.3.4](#134-workflow-correlation-and-causation-identity), [1.7.1](#171-distributed-tracing-across-http-and-kafka) |
| What can Prometheus / Grafana see? Are the logs structured? | [1.7.3](#173-structured-logs-and-metrics) |
| Can you follow one request through Kafka in a tracing tool? | [1.7.1](#171-distributed-tracing-across-http-and-kafka) |
| How do you version event contracts? | [1.3.6](#136-tolerant-readers-and-contract-evolution) |
| How do two people editing the same record at once not overwrite each other? | [1.6.4](#164-concurrency-control) |
| Where are the access tokens kept? Can JavaScript read them? | [2.1.1](#211-oidc-authorization-code--pkce-through-a-bff), [3.3](#33-tokens-in-the-browser) |
| How do you stop a user from opening someone else's record by changing an ID (IDOR / BOLA)? | [2.2.3](#223-object-level-authorization) |
| Is authorization only role-based? | [2.2](#22-authorization-beyond-rbac) |
| How do you stop one person from both initiating and approving? | [2.2.6](#226-separation-of-duties-makerchecker) |
| Is it safe to retry a POST to an external system? Could a timeout open two bank accounts? | [1.6.3](#163-timeouts-retries-and-circuit-breakers) |
| Can a junior officer approve a high-risk customer, or a large payment? | [2.2.4](#224-abac-department-clearance-and-stage-permissions) |
| How do services authenticate to each other? | [2.1.2](#212-separate-human-and-machine-identities) |
| Does signing out of one application sign the user out everywhere? | [2.1.4](#214-single-sign-out) |
| How are XSS, clickjacking and CSRF handled? | [2.3](#23-browser-and-session-security) |
| What stops a malicious file upload? | [2.4.4](#244-file-upload-security) |
| What does an attacker learn from an error response? | [2.4.1](#241-error-responses-that-leak-nothing) |
| If one service is compromised, what can it reach in the database? | [2.4.3](#243-least-privilege-database-users) |
| Is Kafka secured? Could someone publish a fake event? | [2.4.6](#246-kafka-authentication-and-per-topic-acls) |
| Can one user or a script flood an API? | [2.4.7](#247-rate-limiting) |
| Who did what, and can the record be trusted? Could an administrator quietly change it? | [2.4.8](#248-tamper-evident-audit-trail) |
| A service calls another for a person - does the second one still know who it is? | [2.1.2](#212-separate-human-and-machine-identities) |
| Does restarting a BFF sign everybody out? | [2.3.4](#234-session-management), [1.6.1](#161-pod-replacement-and-horizontal-scaling) |
| How does Kubernetes know a pod is healthy, or ready for traffic? | [1.7.2](#172-health-endpoints-for-liveness-and-readiness) |
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
    - [1.4.1 Choreography across Customer Onboarding, KYC, Compliance and Accounts](#141-choreography-across-customer-onboarding-kyc-compliance-and-accounts)
    - [1.4.2 Compensation on rejection: retain, don't delete](#142-compensation-on-rejection-retain-dont-delete)
    - [1.4.3 Orchestration: the Payments saga](#143-orchestration-the-payments-saga)
  - [1.5 Front-end composition](#15-front-end-composition)
    - [1.5.1 Micro-frontends hosted by a business-neutral Shell](#151-micro-frontends-hosted-by-a-business-neutral-shell)
    - [1.5.2 Shell–MFE protocol and the Application Workspace](#152-shellmfe-protocol-and-the-application-workspace)
    - [1.5.3 One design system across independent front ends](#153-one-design-system-across-independent-front-ends)
    - [1.5.4 Real-time notifications over SignalR](#154-real-time-notifications-over-signalr)
  - [1.6 Resilience and scale-out](#16-resilience-and-scale-out)
    - [1.6.1 Pod replacement and horizontal scaling](#161-pod-replacement-and-horizontal-scaling)
    - [1.6.2 Dead-letter handling](#162-dead-letter-handling)
    - [1.6.3 Timeouts, retries and circuit breakers](#163-timeouts-retries-and-circuit-breakers)
    - [1.6.4 Concurrency control](#164-concurrency-control)
    - [1.6.5 Fail-closed configuration](#165-fail-closed-configuration)
  - [1.7 Observability](#17-observability)
    - [1.7.1 Distributed tracing across HTTP and Kafka](#171-distributed-tracing-across-http-and-kafka)
    - [1.7.2 Health endpoints for liveness and readiness](#172-health-endpoints-for-liveness-and-readiness)
    - [1.7.3 Structured logs and metrics](#173-structured-logs-and-metrics)
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
    - [2.4.6 Kafka authentication and per-topic ACLs](#246-kafka-authentication-and-per-topic-acls)
    - [2.4.7 Rate limiting](#247-rate-limiting)
    - [2.4.8 Tamper-evident audit trail](#248-tamper-evident-audit-trail)
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

Each business capability (Customer Onboarding, Customer KYC, Compliance, Accounts, Payments, Notifications, Documents Management) is a bounded context with its own model, language and PostgreSQL database. No context reads or writes another context's tables, and there are no cross-database foreign keys. Contexts refer to each other's records by business identifier only, for example an application's never-repeating `ApplicationRef` (UUID v7) and its `ApplicationNumber`, never by another context's database ID. Collaboration happens only through published events or explicit APIs, so each context can change its schema without coordinating with the others.

**Where to look at:**

- Database scripts, one per context: [EwpCustomerDb.sql](../src/Microservices/CustomerOnboarding/API/CustomerDB/EwpCustomerDb.sql), [EwpKycDb.sql](../src/Microservices/CustomerKyc/API/KycDb/EwpKycDb.sql), [EwpComplianceDb.sql](../src/Microservices/Compliance/API/ComplianceDb/EwpComplianceDb.sql), [EwpAccountsDb.sql](../src/Microservices/Accounts/API/AccountsDb/EwpAccountsDb.sql), [EwpPaymentsDb.sql](../src/Microservices/Payments/API/PaymentsDb/EwpPaymentsDb.sql), [EwpNotificationsDb.sql](../src/Microservices/Notifications/API/NotificationsDb/EwpNotificationsDb.sql), [EwpDocumentsManagementDb.sql](../src/Microservices/DocumentsManagement/API/DocumentMgmtDB/EwpDocumentsManagementDb.sql)
- Cross-context reference by business identifier: `ApplicationRef` in the [`KycCase`](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs) aggregate
- Context map: [Blueprint §5](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#5-bounded-contexts-and-context-map)

#### 1.1.2 Independently deployable components

Every API, BFF, worker and front end is its own deployable with its own configuration and secrets. A micro-frontend and its BFF ship together: the Next.js app is exported as static files and served by its BFF, so the pair can be released and rolled back as one unit without touching the Shell or other contexts. Contexts can even use different stacks: the Customer Onboarding, Compliance, Accounts and Payments BFFs are ASP.NET Core, the Customer KYC BFF is NestJS, and the Audit context follows the customer's pattern: a Next.js app that is both the SPA and a light BFF (server actions), a separately deployed NestJS Journey API, and ASP.NET Core Domain APIs. All sit behind the same Shell and the same protocol. (The existing contexts stay as they are; new contexts follow the customer's pattern.)

**Where to look at:**

- .NET BFFs serving their exported MFEs: [CustomerOnboarding/BFF.Web](../src/Microservices/CustomerOnboarding/BFF.Web), [Compliance/BFF.Web](../src/Microservices/Compliance/BFF.Web/README.md), [Accounts/BFF.Web](../src/Microservices/Accounts/BFF.Web/README.md)
- NestJS BFF serving its exported MFE: [CustomerKyc/BFF.Web](../src/Microservices/CustomerKyc/BFF.Web)
- Build and export of all front ends: [CompileAndExportBFFClients_V3.ps1](../ps/build/CompileAndExportBFFClients_V3.ps1)
- Rules and release checklist: [Blueprint §7](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#7-independent-deployability)

**Not yet:** service URLs and client IDs are still compiled into `Common.Landscape`, so changing one forces a rebuild of the others. Secrets are already per deployable.

#### 1.1.3 Context-owned asynchronous workers

Kafka relays and subscribers belong to the bounded context whose database or API they use, and are versioned and deployed with it. `src/AsyncWorkflows` is a folder, not a shared layer. Each worker is named after its owner and purpose: `KycCaseOpeningSubscriber` (Customer KYC) opens KYC cases from onboarding events, `ComplianceCaseOpeningSubscriber` (Compliance) opens Compliance cases from KYC approvals, `AccountApplicationOpeningSubscriber` (Accounts) opens account applications from Compliance approvals, `OnboardingOutcomeSubscriber` (Customer Onboarding) records KYC and Compliance outcomes on applications, `AccountsCommandSubscriber` (Accounts) carries the Payments saga's funds commands to Accounts, `PaymentsSagaReplySubscriber` (Payments) carries Accounts' replies back to the saga, and `NotificationsSubscriber` (Notifications) turns workflow events into notifications. A worker never writes to a database directly. It calls its own context's API, so every business rule stays in one place.

**Where to look at:**

- [KycCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md), [ComplianceCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/Compliance/ComplianceCaseOpeningSubscriber/README.md), [AccountApplicationOpeningSubscriber](../src/AsyncWorkflows/Subscribers/Accounts/AccountApplicationOpeningSubscriber/README.md), [OnboardingOutcomeSubscriber](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md), [AccountsCommandSubscriber](../src/AsyncWorkflows/Subscribers/Accounts/AccountsCommandSubscriber/README.md), [PaymentsSagaReplySubscriber](../src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/README.md), [NotificationsSubscriber](../src/AsyncWorkflows/Subscribers/Notifications/NotificationsSubscriber/README.md)
- Internal, M2M-only endpoints the workers call: [InternalKycCasesController.cs](../src/Microservices/CustomerKyc/API/Controllers/InternalKycCasesController.cs), [InternalComplianceCasesController.cs](../src/Microservices/Compliance/API/Controllers/InternalComplianceCasesController.cs), [InternalAccountApplicationsController.cs](../src/Microservices/Accounts/API/Controllers/InternalAccountApplicationsController.cs), [InternalOnboardingApplicationsController.cs](../src/Microservices/CustomerOnboarding/API/API/Controllers/InternalOnboardingApplicationsController.cs), [InternalFundsCommandsController.cs](../src/Microservices/Accounts/API/Controllers/InternalFundsCommandsController.cs), [InternalPaymentSagaRepliesController.cs](../src/Microservices/Payments/API/Controllers/InternalPaymentSagaRepliesController.cs)

### 1.2 Domain-Driven Design

#### 1.2.1 Aggregates that enforce their own invariants

Business rules live in aggregates, not in controllers, BFFs or UIs. An aggregate is changed only through its methods. Each method checks the rule, changes state and records a domain event, so an invalid state cannot be reached from outside. `KycCase.DecideStage` enforces the stage workflow, separation of duties and case assignment. `OnboardingApplication` decides which status transitions are allowed and tolerates KYC facts that arrive twice or out of order. `Customer` allows one onboarding at a time (only a PROSPECT starts one; an onboarded customer or one with an application in progress is refused) and locks the KYC-verified name once onboarding starts; a partial unique index backs the one-at-a-time rule against concurrent requests. The screens only reflect these rules (no "Start onboarding" for an onboarded customer, read-only identity fields, decisions disabled with the reason shown): typing a URL, using a stale tab or calling the API directly is refused by the aggregate all the same. Aggregates reference other aggregates by ID only, and take the current time as a parameter (`TimeProvider`), so they are deterministic and testable.

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
- Worker processors: [KycCaseOpeningProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Processing/KycCaseOpeningProcessor.cs), [OnboardingOutcomeProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Processing/OnboardingOutcomeProcessor.cs)

#### 1.3.4 Workflow, correlation and causation identity

Every event carries a `WorkflowId` (the business process), a `CorrelationId` (the end-to-end trace), a `CausationId` (the message or command that caused it) and the `InitiatedByUserId` (the accountable human). These identifiers are copied across every hop, from Customer Onboarding to KYC and back. One onboarding can therefore be reconstructed as a causal chain across databases, from the agent's submission to the KYC officer's decision. Decisions also record `acted_by_user_id`, the human who made them. A worker carries the initiator forward but never impersonates them: the initiator is used for accountability, never as a permission.

**Where to look at:**

- Envelope: [IntegrationEventEnvelope.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/IntegrationEventEnvelope.cs)
- Propagation through a hop: `X-Workflow-Id` / `X-Correlation-Id` / `X-Causation-Id` in [OnboardingOutcomeProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Processing/OnboardingOutcomeProcessor.cs), read by [WorkflowContextAccessor.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/WorkflowContextAccessor.cs)
- A fully traced run: [End-to-End-Processing-Walkthrough.md](End-to-End-Processing-Walkthrough.md)
- Live evidence: the `workflow_id`, `correlation_id`, `causation_id` and `initiated_by` columns of both Outbox tables

Both contexts publish the same standard envelope, and the relays copy these identifiers into Kafka headers as well (`message-id`, `workflow-id`, `correlation-id`, `causation-id`), so tools can inspect messages without parsing bodies. The technical trace is covered in [1.7.1](#171-distributed-tracing-across-http-and-kafka).

#### 1.3.5 Ordering guarantees

Ordering is guaranteed where it matters: per aggregate. Each event is published with its aggregate's ID as the Kafka key, so all events of one case or application land on the same partition, in order. The relays publish only the oldest unpublished message of each aggregate. A message that keeps failing therefore blocks later messages of the *same* aggregate only, never the whole topic. Consumers retry in place instead of skipping ahead. Facts that legitimately arrive on different topics (such as "KYC case created" and "KYC case approved") can still arrive out of order, so the receiving aggregate applies only the transitions that are still outstanding.

**Where to look at:**

- Aggregate ID as the Kafka key, oldest-first per aggregate: [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs), [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Out-of-order tolerance: `RecordKycCaseOpened` / `RecordKycApproved` in [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs)

#### 1.3.6 Tolerant readers and contract evolution

**Snapshots travel with the events, minimised per context.** The applicant's name and residential address are carried by the events that already flow (event-carried state transfer), not looked up from Customer Onboarding when a page loads. Each context stores only what it needs: KYC and Compliance keep name and address (to verify and to screen), Accounts keeps the name only (the account holder), and no context but Customer Onboarding holds contact details. A stored snapshot also keeps the record honest for audit: a case shows the identity that was verified or screened, even if the customer's record changes later, and the officer screens keep working when Customer Onboarding is down.

Consumers read only the fields they need into their own message models and ignore everything else. A producer can therefore add fields without breaking anyone, and no shared contract package couples the producer's and consumer's release cycles. Event changes are additive within a version, and every envelope states its `SchemaVersion`. When KYC moved from a flat message to the standard envelope, its consumer was taught to read both shapes first, so the change needed no coordinated release and no topic reset.

**Where to look at:**

- [OutcomeMessage.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Messages/OutcomeMessage.cs) (reads the envelope and the older flat shape), the private envelope records in [KycCaseOpeningProcessor.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Processing/KycCaseOpeningProcessor.cs)
- Conventions: [Integration-Event-Catalogue.md §2](Integration-Event-Catalogue.md#2-conventions)

### 1.4 Saga pattern

#### 1.4.1 Choreography across Customer Onboarding, KYC, Compliance and Accounts

Customer onboarding is a long-running, choreographed saga with no central coordinator. Each context performs its own local transaction, publishes the fact through its Outbox and reacts to other contexts' facts. Submitting an application causes KYC to open a case. The case being opened moves the application to `KYC_IN_PROGRESS`. The KYC decision moves it to `KYC_COMPLETED` or `REJECTED`. A KYC approval opens a Compliance case, which moves the application to `COMPLIANCE_IN_PROGRESS`; the Compliance decision moves it to `COMPLIANCE_COMPLETED` or `REJECTED`. A Compliance approval opens an account application (`ACCOUNT_OPENING_IN_PROGRESS`); when the account officer approves and the core-banking system opens the account, the onboarding is `COMPLETED` — the saga ends. Human review is a persisted state, not a waiting process: nothing stays in memory while an officer is away for days. The officer's decision is a new transaction that resumes the workflow. Each context changes only the state it owns.

**Where to look at:**

- Design and rules: [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md)
- Forward hops: [KycCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md), [ComplianceCaseOpeningSubscriber](../src/AsyncWorkflows/Subscribers/Compliance/ComplianceCaseOpeningSubscriber/README.md), [AccountApplicationOpeningSubscriber](../src/AsyncWorkflows/Subscribers/Accounts/AccountApplicationOpeningSubscriber/README.md); return hop for all three: [OnboardingOutcomeSubscriber](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md)
- Live evidence: the CO, KYC, Compliance and Accounts Outbox tables of one onboarding, linked by `causation_id`

Payments uses the other style, orchestration: see [1.4.3](#143-orchestration-the-payments-saga).

#### 1.4.2 Compensation on rejection: retain, don't delete

A saga cannot roll back a distributed transaction; it **compensates** instead. Each context undoes or neutralises its own work, and none touches another context's database. When KYC, Compliance or Accounts rejects an onboarding application, Customer Onboarding (the owner of the application's state) marks it REJECTED and publishes `OnboardingApplicationRejected`. That event names exactly the evidence documents recorded when the application was submitted. (The submission already made Documents Management mark those documents **ATTACHED**: from then on they are KYC records that cannot be deleted.) Documents Management's own subscriber then marks those documents **INVALIDATED**. They are retained rather than deleted, because a bank must keep evidence for audit and regulatory retention. Deleting an invalidated document is refused. The compensation is idempotent (Inbox, and invalidating twice changes nothing), and it is scoped: an event can only invalidate documents of the application's own branch.

**Where to look at:**

- The rejection naming the evidence: `Reject` in [OnboardingApplication.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs), [OnboardingApplicationRejectedIntegrationEvent.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Messaging/OnboardingApplicationRejectedIntegrationEvent.cs)
- The compensating participant: [DocumentInvalidationSubscriber](../src/AsyncWorkflows/Subscribers/DocumentsManagement/DocumentInvalidationSubscriber/README.md), [InvalidateDocumentsCommand.cs](../src/Microservices/DocumentsManagement/API/Application/Documents/Commands/InvalidateDocuments/InvalidateDocumentsCommand.cs)
- Retention in the aggregate: `Attach` / `Invalidate` / `CanBeRemoved` in [Document.cs](../src/Microservices/DocumentsManagement/API/Domain/Aggregates/Document.cs)
- Design: [Saga plan §1.5](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#15-rejection-and-compensation)

**Later failures are compensated too.** When the account cannot be opened after every officer approved (core banking refuses, or fails six times), Accounts publishes `AccountOpeningFailed`. Customer Onboarding records COMPENSATING and then REJECTED (`RejectedBy: ACCOUNT_OPENING`), so the same `OnboardingApplicationRejected` invalidates the evidence and the customer becomes a prospect again. No approval is "rolled back": each context keeps its decision on record; only the onboarding ends.

**Not yet:** disposal after the retention period.

#### 1.4.3 Orchestration: the Payments saga

A payment touches money in two places that must agree: the customer's account (Accounts) and the payment network. Here one coordinator, the **Payments saga**, decides every step: reserve the funds in Accounts, wait for a payments officer's approval when the amount is above the tier, send the payment to the network, then settle the funds. The saga is an aggregate stored in the Payments database, not a process that waits in memory. `POST /v1/payments` saves the payment, the saga and the first command in one transaction and answers **202 Accepted** at once; every later step is a short, separate transaction (a reply arrived, a timer fell due, an officer decided). Commands travel to Accounts and replies come back through Kafka, using the same Outbox, Inbox and courier workers as the rest of the platform. Accounts never knows about the saga; it only keeps one idempotent **funds hold** per payment.

**Nothing is lost when a step fails.** If the network refuses the payment, the saga **compensates**: it releases the hold, and the payment ends FAILED with the money back in the account. A missing reply is resent with a doubling timeout; Accounts answers a repeated command with the same result, so a resend never moves money twice. If even the release cannot be confirmed, the saga does not pretend: the payment becomes **COMPENSATION_FAILED**, readiness turns Degraded, operations staff are notified, and they can release it again from the Payment Processing Monitor. A late confirmation still resolves it.

**One payment per request.** The screen creates one `Idempotency-Key` per payment form; a double-click or a retried POST returns the payment already started rather than a second one.

**Where to look at:**

- The state machine: [PaymentSaga.cs](../src/Microservices/Payments/API/Domain/Aggregates/PaymentSaga.cs) (`OnFundsReserved`, `OnNetworkRefused`, `OnReplyTimeout`, `RetryCompensation`, …); the payment: [Payment.cs](../src/Microservices/Payments/API/Domain/Aggregates/Payment.cs)
- Timers and network calls: [SagaStepRunner.cs](../src/Microservices/Payments/API/Infrastructure/Saga/SagaStepRunner.cs), [RunDueSagaStep.cs](../src/Microservices/Payments/API/Application/Commands/RunDueSagaStep.cs)
- The participant: [FundsHold.cs](../src/Microservices/Accounts/API/Domain/Aggregates/FundsHold.cs), [FundsCommand.cs](../src/Microservices/Accounts/API/Application/Commands/FundsCommand.cs)
- One payment per key: `Idempotency-Key` in [PaymentsController.cs](../src/Microservices/Payments/API/Controllers/PaymentsController.cs) and [InitiatePayment.cs](../src/Microservices/Payments/API/Application/Commands/InitiatePayment.cs)
- Live evidence: the payment's status page shows the saga timeline (`payment_saga_history`)
- Design, diagram and failure table: [Saga plan §2](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments); business rules: [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md)

### 1.5 Front-end composition

#### 1.5.1 Micro-frontends hosted by a business-neutral Shell

The Shell provides branding, sign-in, the menu and the Application Workspace, and hosts each context's micro-frontend in an iframe. It holds no business logic: the menu comes from the Shell's database, so adding a context adds menu rows, not Shell code. The iframe gives each MFE its own origin, cookies and security boundary, so one MFE cannot read another's DOM or session.

**Where to look at:**

- Shell host page: [page.tsx](../src/Shell/client-app/app/page.tsx); menu: [MenuController.cs](../src/Shell/Controllers/MenuController.cs), [EwpBssShellDb.sql](../src/Shell/MenuDB/EwpBssShellDb.sql)
- Requirements: [Shell-Requirements.md](../src/Shell/doc/Shell-Requirements.md)

#### 1.5.2 Shell–MFE protocol and the Application Workspace

The Shell and the MFEs talk through an explicit `postMessage` protocol: ready, context hand-over, context update, and navigation request/response. The protocol lets an MFE with unsaved changes ask the user before the Shell navigates away. Whenever the user picks a record, the MFE publishes it, and the Shell's Application Workspace shows it above the iframe and hands it to the next MFE. To the Shell the context is opaque: it never interprets business data, and a receiving MFE re-reads anything authoritative from its own BFF. Both sides check the sender's origin and window and post only to an explicit origin; the MFE's trusted parent comes from a static allow-list, never from `document.referrer`.

**Where to look at:**

- MFE side: [MfeShell.tsx (KYC)](../src/Microservices/CustomerKyc/BFF.Web/client-app/app/components/MfeShell.tsx), [MfeShell.tsx (CO)](../src/Microservices/CustomerOnboarding/BFF.Web/client-app/app/components/MfeShell.tsx), [MfeShell.tsx (CMP)](../src/Microservices/Compliance/BFF.Web/client-app/app/components/MfeShell.tsx)
- Shell side: [page.tsx](../src/Shell/client-app/app/page.tsx), [ApplicationWorkspace.tsx](../src/Shell/client-app/app/components/ApplicationWorkspace.tsx)
- Protocol and selection rules: [Shell-Requirements.md §3–4](../src/Shell/doc/Shell-Requirements.md#3-application-workspace)

#### 1.5.3 One design system across independent front ends

All front ends share one set of design tokens (colours, typography, radii, shadows) as plain CSS variables. The MFEs use them through Tailwind CSS v4, and the IDP's Razor pages use them directly. The IDP copies the tokens at build time, so every front end stays independently deployable while looking like one product. Styles are compiled or served as files, with no CSS-in-JS and no CDN, which keeps the strict Content-Security-Policy intact.

**Where to look at:**

- Tokens and Tailwind mapping: [src/Common/DesignSystem](../src/Common/DesignSystem/README.md)
- Copy at build time: the `CopyDesignSystemAssets` target in [EnterpriseWebPlatform.IdentityServer.csproj](../src/IDP/EnterpriseWebPlatform.IdentityServer.csproj)

#### 1.5.4 Real-time notifications over SignalR

When the workflow moves, the right people are told at once: the agent who started the onboarding hears about each decision ("etpar approved KYC for Camilla Parkers"), and the officers of the next team hear about new work in their branch ("New KYC case"). A separate Notifications context does this; the Shell stays business-neutral and never connects to Kafka. Each notification is **stored before it is pushed**, with an Inbox, so a dropped connection or an offline user loses nothing. **Who receives what is decided on the server from the token**: a connection joins only its own person and its role in its branch, and offers the browser no way to join anything else. The browser never holds a token: the Shell BFF proxies both the REST API and the SignalR hub and adds the access token, and because a browser cannot send the anti-forgery header on a WebSocket, the hub route checks the request's `Origin` instead (cross-site WebSocket hijacking).

**Where to look at:**

- Rules and audiences: [NotificationRules.cs](../src/Microservices/Notifications/API/Domain/NotificationRules.cs), [Notification.cs](../src/Microservices/Notifications/API/Domain/Notification.cs)
- Hub and store-then-push: [NotificationsHub.cs](../src/Microservices/Notifications/API/Hubs/NotificationsHub.cs), [PublishFromEvent.cs](../src/Microservices/Notifications/API/Application/PublishFromEvent.cs)
- The worker: [NotificationsSubscriber](../src/AsyncWorkflows/Subscribers/Notifications/NotificationsSubscriber/README.md); the proxy and Origin check: [Shell Program.cs](../src/Shell/Program.cs)
- The Shell UI: [NotificationBell.tsx](../src/Shell/client-app/app/components/NotificationBell.tsx), [useNotifications.ts](../src/Shell/client-app/app/hooks/useNotifications.ts); the MFE side: `useShellNotifications` in each MFE's `MfeShell.tsx`

In the Shell, a bell with the unread count and live toasts show them; the Shell holds the only connection per browser and relays each notification to the MFE in the frame, so an officer's work queue reloads by itself when new work arrives. A connection never outlives its access token: the hub closes it at expiry and the client reconnects through the BFF with a fresh one.

Clicking a notification opens the record (4c): new work opens the case or application, progress opens the application list. The Shell finds the server in the person's own menu (the notification holds only a path), asks the current MFE first so unsaved changes are protected, and the target BFF accepts the path only from its allow-list.

**Not yet:** a SignalR backplane for more than one API instance.

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

No step relies on in-memory state surviving a restart: workflow state lives in the databases and in Kafka offsets. The same holds for every BFF: the .NET BFFs' sessions and the Data Protection keys that encrypt their cookies live in PostgreSQL (`EwpBffStateDb`, one schema per BFF), so a restarted or second BFF instance keeps everybody signed in.

**Where to look at:**

- Claiming rows: the `FOR UPDATE SKIP LOCKED` queries in [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs) and [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Commit after processing, clean group exit: [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)
- Flush on shutdown: [KafkaProducer.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaProducer.cs)

Every component also exposes liveness and readiness endpoints for the orchestrator's probes ([1.7.2](#172-health-endpoints-for-liveness-and-readiness)), so a stuck instance is restarted and a starting one receives no work until it is ready.

The KYC (NestJS) BFF and the Audit web app keep their sessions in the same database (schemas `kyc_bff` and `audit_bff`), encrypted at rest, so they too can run as several instances.

#### 1.6.2 Dead-letter handling

Two kinds of failure are told apart. **Transient** failures (a dependency is down) are retried in place and never dead-lettered. **Permanent** failures (malformed JSON, an unknown event type, missing required fields, a 4xx rejection from the API) can never succeed. Such a message is copied unchanged to the worker's dead-letter topic, with headers recording the reason, the original topic, partition and offset, the consumer group and the time; then it is committed, so the partition keeps flowing. If writing to the dead-letter topic itself fails, the message is retried rather than lost. On the publishing side, an Outbox row that fails ten times is **parked** (it stays unpublished with `attempt_count` and `last_error` for an operator) instead of being retried forever.

**Where to look at:**

- Dead-letter step: `DeadLetterAsync` in [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)
- Topics `customer-kyc.case-opening-subscriber.dlq` and `customer-onboarding.outcome-subscriber.dlq`: [KafkaTopicNames.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaTopicNames.cs), [Integration-Event-Catalogue.md §4.6](Integration-Event-Catalogue.md#46-dead-letter-topics)
- Parked Outbox rows: `MaxAttempts` in [CustomerOutboxPublisherOptions.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Configuration/CustomerOutboxPublisherOptions.cs)

Parked Outbox rows are not silent either: the relay's readiness check turns *Degraded* while any row is parked or has waited more than two minutes ([1.7.2](#172-health-endpoints-for-liveness-and-readiness)).

**Not yet:** a replay tool for dead-lettered messages, and alerting on dead-letter topic growth.

#### 1.6.3 Timeouts, retries and circuit breakers

Every call from a worker to an API runs through a resilience pipeline: a 10-second timeout per attempt, three retries with exponential back-off and jitter, a circuit breaker and a 60-second total budget. When half of the recent calls fail, the circuit opens for 30 seconds, so a struggling API gets room to recover instead of being hammered by every retry. Retrying a POST is safe here only because the endpoints are idempotent (Inbox). Where an endpoint is *not* idempotent, it is deliberately not retried: every BFF retries only GET requests, and the KYC BFF sends an officer's decision exactly once (a lost answer is reported as such, never resent into a misleading "already decided").

**Where to look at:**

- Pipelines (Microsoft.Extensions.Http.Resilience / Polly): `AddStandardResilienceHandler` in the [KycCaseOpeningSubscriber Program.cs](../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Program.cs) and [OnboardingOutcomeSubscriber Program.cs](../src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Program.cs)
- GET-only retries in the BFFs: `options.Retry.DisableForUnsafeHttpMethods()` in [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs); a decision sent once: `postOnce` in [kyc-api.service.ts](../src/Microservices/CustomerKyc/BFF.Web/src/services/kyc-api.service.ts)

**An external provider that is slow or down.** Compliance screens every customer against a third-party AML / sanctions provider, simulated by the [Screening Provider Simulator](../src/Simulators/ScreeningProviderSimulator/README.md), which can be switched to Slow, Failing or Down while the platform runs. Screening is never done inside a user request or a Kafka handler: a background worker picks up due cases and calls the provider through its own pipeline (5-second attempt timeout, two retries, a circuit breaker that opens for 30 seconds when half of the recent calls fail, 20-second budget). A failure is **never a pass**: the case stays in `SCREENING` and is retried later with back-off (15 s doubling to 5 minutes), and while the circuit is open the worker does not call the provider at all. Readiness turns Degraded when a case has waited more than two minutes, so operations see the backlog. When the provider recovers, waiting cases are screened automatically and nothing is lost.

- Worker and back-off: [ScreeningWorker.cs](../src/Microservices/Compliance/API/Infrastructure/Screening/ScreeningWorker.cs), [ScreenDueCase.cs](../src/Microservices/Compliance/API/Application/Commands/ScreenDueCase.cs); "failure is not a pass": `RecordScreeningFailure` in [ComplianceCase.cs](../src/Microservices/Compliance/API/Domain/Aggregates/ComplianceCase.cs)
- Provider pipeline and anti-corruption mapping: [Compliance API Program.cs](../src/Microservices/Compliance/API/Program.cs), [ScreeningProviderClient.cs](../src/Microservices/Compliance/API/Infrastructure/Screening/ScreeningProviderClient.cs)
- Demo: ReadMe.txt section 7b

**Retrying a POST without opening two accounts.** Accounts asks the bank's core-banking system (simulated by the [Core Banking Simulator](../src/Simulators/CoreBankingSimulator/README.md)) to open the account, through the same kind of worker, back-off and circuit breaker. Unlike a screening, opening an account is **not** naturally repeatable: if the answer to a successful request is lost in a timeout, a blind retry could open a second account. Every request therefore carries an **`Idempotency-Key`** (the onboarding's `ApplicationRef`), and core banking returns the same account for a repeated key — which is the only reason this POST may be retried at all. A refusal (HTTP 422) is a business answer: no retry, the application is FAILED. Too many technical failures end the same way, and compensation follows.

- Idempotent client and pipeline: [CoreBankingClient.cs](../src/Microservices/Accounts/API/Infrastructure/CoreBanking/CoreBankingClient.cs), [Accounts API Program.cs](../src/Microservices/Accounts/API/Program.cs); worker: [OpenDueAccount.cs](../src/Microservices/Accounts/API/Application/Commands/OpenDueAccount.cs)
- Retry vs give up vs refusal: `RecordOpeningFailure` / `RecordOpeningRefused` in [AccountApplication.cs](../src/Microservices/Accounts/API/Domain/Aggregates/AccountApplication.cs)

The Compliance and Accounts BFFs also put their calls to their APIs behind timeouts and a circuit breaker, retrying GETs only; when the API is down, the officer sees "temporarily unavailable" at once instead of a hanging page ([Compliance BFF Program.cs](../src/Microservices/Compliance/BFF.Web/Program.cs)).

The KYC BFF (NestJS) does the same with its own breaker per downstream service (KYC API, Documents Management, the IDP's token endpoint; half of the calls in 30 seconds failing opens it for 30 seconds), timeouts and GET-only retries: [resilient-fetch.ts](../src/Microservices/CustomerKyc/BFF.Web/src/resilience/resilient-fetch.ts).

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

### 1.7 Observability

#### 1.7.1 Distributed tracing across HTTP and Kafka

Every .NET component uses OpenTelemetry with W3C trace context. A trace normally ends where a message is put on a queue. Here it does not. The request's trace context is stored on the Outbox row, so the relay, possibly seconds later, publishes in a span that continues that trace and sends it in the Kafka `traceparent` header. The subscriber processes the message in a child span, and its HTTP call carries the trace to the next API. One onboarding therefore appears as **one trace** in Jaeger, Grafana Tempo, Azure Monitor or AWS X-Ray: from the agent's click in the CO BFF, through the CO API, the relay, the KYC worker, the KYC API and back to the CO API, with database calls as child spans. Background work keeps the trace too: the Accounts API stores the officer's approval trace with the application, and the worker that later opens the account continues it, so the core-banking call and `AccountOpened` / `AccountOpeningFailed` belong to the approval's trace. Log lines carry the same TraceId. Spans are exported over OTLP only when an endpoint is configured; otherwise the context is still propagated.

**Where to look at:**

- Shared setup and messaging spans: [ObservabilityExtensions.cs](../src/Common/Observability/ObservabilityExtensions.cs), [MessagingTelemetry.cs](../src/Common/Observability/MessagingTelemetry.cs)
- Trace stored with the event: `trace_parent` in [EwpCustomerDb.sql](../src/Microservices/CustomerOnboarding/API/CustomerDB/EwpCustomerDb.sql) and [EwpKycDb.sql](../src/Microservices/CustomerKyc/API/KycDb/EwpKycDb.sql)
- Publish span and headers: [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs), [KycOutboxPublisher.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs)
- Process span: [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)
- How to view traces locally (Jaeger): [ReadMe.txt §9b](../ReadMe.txt)

The KYC BFF (NestJS) continues the caller's trace (or starts one) and passes `traceparent` to the KYC API and Documents Management, so an officer's decision is one trace too ([trace-context.ts](../src/Microservices/CustomerKyc/BFF.Web/src/observability/trace-context.ts)).

**Not yet:** the Node.js services export no spans of their own (only the context is propagated), and the Audit Journey API and Audit web app do not propagate it yet.

#### 1.7.2 Health endpoints for liveness and readiness

Every component answers the two questions an orchestrator such as AKS or EKS asks. `/health/live` asks whether the process is working; if not, restart it. `/health/ready` asks whether it can do its job now; if not, send it no traffic yet. The two are deliberately different. A database outage makes an API *not ready* but still *live*, because restarting it would not help. A relay or consume loop that stops cycling fails *liveness*, so it is restarted. Workers have no web server of their own, so they serve the same endpoints from a minimal built-in listener. A third state, *Degraded*, reports "working, but look": a relay with parked or overdue Outbox messages, or a subscriber retrying a message while its downstream API is down. Responses list each check's status and nothing internal.

**Where to look at:**

- Shared endpoints and worker listener: [HealthEndpoints.cs](../src/Common/Observability/HealthEndpoints.cs)
- Loop heartbeat and Outbox backlog checks: [LoopHeartbeat.cs](../src/Common/Observability/LoopHeartbeat.cs), [OutboxBacklogHealthCheck.cs](../src/Common/Observability/OutboxBacklogHealthCheck.cs)
- Subscriber liveness and readiness: [SubscriberHealth.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/SubscriberHealth.cs)
- Wiring: the `AddHealthChecks` calls in each `Program.cs`, e.g. [CustomerKyc API Program.cs](../src/Microservices/CustomerKyc/API/Program.cs); endpoint list: [ReadMe.txt §9a](../ReadMe.txt)

#### 1.7.3 Structured logs and metrics

The three signals are set up once, in the shared observability library, and every .NET component gets them the same way. **Logs** go through Serilog: every entry carries the service name and the trace ID, so a log line leads straight to its trace; each HTTP request is one line with method, path, status, duration and the caller (a subject ID or client ID, never a name, a query string or a token). Development shows readable text; elsewhere each entry is one JSON object, ready for a log shipper. **Metrics** follow OpenTelemetry and are scraped by Prometheus at `/metrics` (workers serve it on their health port): request rates and durations, rate-limited requests, sign-ins and authorization decisions, retries and circuit breakers, database connections and the .NET runtime come from what .NET already measures. EWP adds its own: Kafka messages published and consumed by outcome (including dead-lettered), and every health check's status and numbers, so a Grafana panel or an alert sees exactly what the readiness probe sees, such as a payment whose compensation failed or a growing Outbox backlog. With an OTLP endpoint configured, logs and metrics travel with the traces to a collector (Grafana, Observe, Jaeger).

**Where to look at:**

- Set-up for all three signals: [ObservabilityExtensions.cs](../src/Common/Observability/ObservabilityExtensions.cs); logging and request logging: [EwpLogging.cs](../src/Common/Observability/EwpLogging.cs)
- `/metrics` and the health-to-metrics bridge: [MetricsEndpoints.cs](../src/Common/Observability/MetricsEndpoints.cs); worker listener: [HealthEndpoints.cs](../src/Common/Observability/HealthEndpoints.cs)
- Kafka counters: [KafkaProducer.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaProducer.cs), `RecordConsumed` in [MessagingTelemetry.cs](../src/Common/Observability/MessagingTelemetry.cs)
- Metric names and how to look at them: [ReadMe.txt §9c](../ReadMe.txt)

The KYC BFF (NestJS) follows the same rules: the same log formats and request line, and `/metrics` with the same metric names plus its circuit breakers and token exchanges ([logger.ts](../src/Microservices/CustomerKyc/BFF.Web/src/observability/logger.ts), [metrics.ts](../src/Microservices/CustomerKyc/BFF.Web/src/observability/metrics.ts)).

**Not yet:** dashboards and alert rules (they belong to the deployment's Grafana / Observe); logs and metrics in the Audit Journey API and Audit web app.

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

**Delegated user context (token exchange, RFC 8693).** Where a service calls another **for a person**, a plain machine token would lose who that person is. The Audit context therefore uses token exchange at every hop: the caller swaps the token it holds for one aimed at the next service, in which the person stays the subject (so roles, permissions and branch still decide) and the acting services are listed in a nested `act` claim. The Audit API accepts a token only when the person is an auditor **and** the acting client is the Audit Journey API; the IDP decides, by an allow-list, which client may exchange which token, and the Journey API has no grant other than token exchange, so it can never act without a person.

- Grant and allow-list: [TokenExchangeGrantValidator.cs](../src/IDP/Security/TokenExchangeGrantValidator.cs); `act` in the access token: [CustomProfileService.cs](../src/IDP/Services/CustomProfileService.cs)
- Checking person and caller: [DelegatedAuditorAuthorization.cs](../src/Microservices/Audit/API/Authorization/DelegatedAuditorAuthorization.cs); the Journey API side: [Audit Journey API README](../src/Microservices/Audit/JourneyApi/README.md)

**Documents Management** works the same way. The Customer Onboarding BFF (upload, and clean-up of an unfinished submission) and the KYC BFF (reading evidence) each exchange the signed-in person's token for a short-lived Documents Management token: write for Customer Onboarding, read only for KYC. Documents Management takes the branch from that token, as issued by the IDP, and accepts a person only when one of those two BFFs is the acting client; only Customer Onboarding may delete. No service asserts a person's branch on their behalf any more, and a person's own token, or a machine token, gets no documents.

- Branch from the token, acting client checked: [DocumentResourceAuthorization.cs](../src/Microservices/DocumentsManagement/API/Authorization/DocumentResourceAuthorization.cs)
- The exchanges: [DocumentsManagementTokenService.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Services/DocumentsManagementTokenService.cs) (CO BFF), [documents-management-token.service.ts](../src/Microservices/CustomerKyc/BFF.Web/src/services/documents-management-token.service.ts) (KYC BFF)

#### 2.1.3 Identity provider hardening

The IDP (Duende IdentityServer 8) applies the standard defences of a sign-in service:

- **Brute force:** per-account lockout (15 minutes) and per-IP login throttling.
- **Username enumeration:** an unknown username costs the same time as a wrong password (a dummy hash is verified), and both produce the same message.
- **Grants:** no implicit and no password (ROPC) grant.
- **Logout:** POST only, so it cannot be triggered cross-site.
- **Pages:** framing is forbidden (`frame-ancestors 'none'`); the pages use no CDN and no inline script or style, so their CSP has no `'unsafe-inline'`.
- **Passwords:** password managers are supported (`autocomplete="current-password"`).
- **State that survives:** refresh tokens (only a hash of each is stored, the details encrypted), pushed authorization requests and signing keys are kept in PostgreSQL by Duende's operational store, with hourly clean-up. An IDP restart signs nobody out, and several IDP instances share them.
- **Sign-in sessions on the server:** each IDP sign-in session is a row in PostgreSQL (Duende's server-side sessions); the browser's cookie only refers to it. Signing out deletes the row, so a copy of the cookie taken before sign-out (malware, a shared machine) is refused afterwards instead of working for up to ten hours; an IDP session that expires also ends the clients' sessions through back-channel logout.
- **Keys never stored readable:** the IDP's and every .NET BFF's Data Protection key ring is encrypted at rest - with a certificate (`DataProtection:CertificatePath`), or DPAPI in Development on Windows. Without either, the application refuses to start.

**Where to look at:**

- Lockout and dummy hash: [UserRepository.cs](../src/IDP/Repositories/UserRepository.cs), [PasswordManager.cs](../src/IDP/Security/PasswordManager.cs)
- Throttling: `AddRateLimiter` in [IDP Program.cs](../src/IDP/Program.cs)
- Headers and CSP: [SecurityHeadersAttribute.cs](../src/IDP/SecurityHeadersAttribute.cs)
- Operational store and server-side sessions: `AddOperationalStore`, `AddServerSideSessions` in [IDP Program.cs](../src/IDP/Program.cs), tables in [IdentityAccessDb.sql](../src/IDP/IdentityAccessDB/IdentityAccessDb.sql) (schema `identity_server`)
- Key-ring encryption, fail closed: [PersistentDataProtection.cs](../src/Common/WebUtilities/Security/PersistentDataProtection.cs)

**Two-step sign-in and step-up (MFA), switched off by default.** With `Mfa:Enabled` on, every user signs in with the password AND the 6-digit code from Google Authenticator; someone without one enrols at their next sign-in (QR code, one confirming code, ten single-use recovery codes). The authenticator secret is stored only encrypted with the IDP's key ring, recovery codes only as hashes; a wrong code counts towards the lockout, and a code is never accepted twice. Every token then says how the person signed in (`amr`), and the APIs require `mfa` for the risky actions - a payments officer's approval or rejection, operations' "Retry release", and the KYC, Compliance and account-opening decisions - answering 403 with a clear reason otherwise (step-up, in the spirit of RFC 9470).

- Sign-in steps: [Login.cshtml.cs](../src/IDP/Pages/Account/Login.cshtml.cs), [Mfa.cshtml.cs](../src/IDP/Pages/Account/Mfa.cshtml.cs), [MfaSetup.cshtml.cs](../src/IDP/Pages/Account/MfaSetup.cshtml.cs); codes and secrets: [Totp.cs](../src/IDP/Security/Totp.cs), [MfaService.cs](../src/IDP/Security/MfaService.cs)
- Step-up in the APIs: [MfaStepUp.cs](../src/Common/WebUtilities/Security/MfaStepUp.cs), and `stepUpPolicies` in the Payments, Accounts, Compliance and KYC APIs' `Program.cs`
- How to try it: [ReadMe.txt §7f](../ReadMe.txt)

**Not yet:** passkeys / FIDO2 (phishing-resistant), and remembering a trusted device (deliberately off).

#### 2.1.4 Single sign-out

Signing out ends the session everywhere, on the server too: the IDP deletes its own session record, so its sign-in cookie cannot be replayed. The IDP notifies every client: through the browser (front-channel, a hidden iframe on the signed-out page) and server-to-server (back-channel, a signed logout token posted to each BFF). Back-channel logout works even when the browser blocks third-party iframes. The KYC BFF also revokes its refresh token at logout, so the token cannot be used after the session ends.

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

Having the right role is not enough to open a *specific* record. Every read, list and write checks that the record lies within the caller's scope, so changing an ID in a URL returns nothing (the defence against OWASP API Security's #1 risk, BOLA / IDOR). A customer service agent sees only customers whose primary residential address is in their branch's city. KYC, Compliance and account officers see and decide only cases of their own branch, and staff see and decide only payments of their own branch. Operations staff are the deliberate exception for payments: they read every branch (to watch processing), but cannot decide. Auditors never use the operational screens: they see every context's activity through the Audit context only (the Audit Trail), where every look is itself recorded, and the Payments API answers an auditor only when the Audit Journey API is the acting client (token exchange). Documents are branch-scoped on every operation. Lists are filtered and paged **in the database**, so out-of-scope rows are never even loaded. A caller without the needed attributes sees nothing (fail closed).

**Where to look at:**

- [CustomerAccessScope.cs](../src/Microservices/CustomerOnboarding/API/Application/Abstractions/Authorization/CustomerAccessScope.cs), [CustomerResourceAuthorization.cs](../src/Microservices/CustomerOnboarding/API/Authorization/CustomerResourceAuthorization.cs)
- [DocumentResourceAuthorization.cs](../src/Microservices/DocumentsManagement/API/Authorization/DocumentResourceAuthorization.cs)
- KYC branch scope: [KycCaseDecisionAuthorization.cs](../src/Microservices/CustomerKyc/API/Authorization/KycCaseDecisionAuthorization.cs), [KycCaseQueries.cs](../src/Microservices/CustomerKyc/API/Infrastructure/KycCaseQueries.cs)
- Payments branch scope and the all-branches readers: `ReadScopeOf` in [PaymentsAuthorization.cs](../src/Microservices/Payments/API/Authorization/PaymentsAuthorization.cs)

#### 2.2.4 ABAC: department, clearance and stage permissions

Attributes of the user, not only their role, decide what they may do. To decide a KYC stage, an officer needs the `kyc_officer` role, the KYC department, clearance level 3 or above, the decision permission (approve or reject) **and** the permission for that stage (`kyc.identity.verify` or `kyc.document.verify`). Different officers can therefore be entitled to different steps of the same case.

In Compliance the required clearance depends on the **case's risk**, not only on the user: a CLEAR screening (LOW risk) can be approved at clearance 3, a POTENTIAL_MATCH (MEDIUM) needs 4 and a MATCH (HIGH) needs 5. Any compliance officer may reject. The rule lives in the aggregate, so the API, a future UI and any other caller get the same answer.

Payments works the same way with **amounts**: a payments officer approves only up to their clearance's limit (3: AUD 10,000; 4: AUD 100,000; 5: any amount). The limits are configuration (`ApprovalLimits`), the check is in the saga aggregate.

**Where to look at:**

- Requirement and handler: [KycCaseDecisionAuthorization.cs](../src/Microservices/CustomerKyc/API/Authorization/KycCaseDecisionAuthorization.cs)
- Stage policies `KycIdentityApprove`, `KycDocumentReject`, …: [CustomerKyc API Program.cs](../src/Microservices/CustomerKyc/API/Program.cs)
- Claims issued by the IDP: [CustomProfileService.cs](../src/IDP/Services/CustomProfileService.cs)
- Clearance by risk: `RiskPolicy` in [ComplianceCodes.cs](../src/Microservices/Compliance/API/Domain/ValueObjects/ComplianceCodes.cs), `Approve` in [ComplianceCase.cs](../src/Microservices/Compliance/API/Domain/Aggregates/ComplianceCase.cs); officer attributes: [ComplianceOfficerAuthorization.cs](../src/Microservices/Compliance/API/Authorization/ComplianceOfficerAuthorization.cs)
- Approval limit by clearance: `ApprovalLimits` in [PaymentDetails.cs](../src/Microservices/Payments/API/Domain/ValueObjects/PaymentDetails.cs), `OnApproved` in [PaymentSaga.cs](../src/Microservices/Payments/API/Domain/Aggregates/PaymentSaga.cs)

#### 2.2.5 ReBAC: relationships owned by each context

Some decisions depend on the relationship between a user and a specific record, not on the user's role alone. These relationships are stored and enforced by the context that owns the record, not by the IDP. In Customer Onboarding, each customer has a **managing agent**: only that agent may update the customer and open or submit applications for them. In Customer KYC, Compliance and Accounts, each case has an **assigned officer**. The first decision, or an explicit claim, assigns the case; only the assignee may decide it, and the assignee may release it. The rule is enforced in the API and the aggregate, so hiding a button in the UI is never what protects the record.

**Where to look at:**

- CO managing agent: [Customer.cs](../src/Microservices/CustomerOnboarding/API/Domain/Aggregates/Customer.cs), [CustomerResourceAuthorization.cs](../src/Microservices/CustomerOnboarding/API/Authorization/CustomerResourceAuthorization.cs)
- KYC assigned officer: [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs), [AssignKycCaseCommandHandler.cs](../src/Microservices/CustomerKyc/API/Application/Commands/AssignKycCase/AssignKycCaseCommandHandler.cs)
- Compliance assigned officer: `Claim` / `Release` in [ComplianceCase.cs](../src/Microservices/Compliance/API/Domain/Aggregates/ComplianceCase.cs), endpoints in [ComplianceCasesController.cs](../src/Microservices/Compliance/API/Controllers/ComplianceCasesController.cs)
- Claim / release endpoints (policy `KycCaseAssign`): [KycCasesController.cs](../src/Microservices/CustomerKyc/API/Controllers/KycCasesController.cs)
- Model and rules: [Authorization-Model.md](Authorization-Model.md)

#### 2.2.6 Separation of Duties (maker–checker)

The person who starts a workflow can never approve it. The agent who submits an onboarding application can neither take nor decide its KYC case. The rule lives inside the `KycCase` aggregate, so no code path can bypass it. It also fails closed: if the initiator is unknown, every decision is denied rather than allowed.

The rule also spans contexts. A Compliance case may not be handled by the initiator **nor by either officer who decided the KYC stages** of the same application, so one person can never both verify a customer and clear them for financial crime. KYC publishes who decided each stage on `kyc.case.approved`; Compliance stores them on the case and checks them in the aggregate. In the same way, the account officer may be neither the initiator nor the Compliance officer who approved the application (the approver travels on `compliance.case.approved`). A payment above the approval tier is decided by a payments officer who is never the person who started it.

**Where to look at:**

- `EnsureSeparationOfDuties` in [KycCase.cs](../src/Microservices/CustomerKyc/API/Domain/Aggregates/KycCase.cs)
- Payments: `EnsureMayDecide` in [Payment.cs](../src/Microservices/Payments/API/Domain/Aggregates/Payment.cs)
- Cross-context SoD: `EnsureOfficerMayAct` in [ComplianceCase.cs](../src/Microservices/Compliance/API/Domain/Aggregates/ComplianceCase.cs) and [AccountApplication.cs](../src/Microservices/Accounts/API/Domain/Aggregates/AccountApplication.cs); the stage deciders on the event: [KycIntegrationEvents.cs](../src/Microservices/CustomerKyc/API/Infrastructure/Messaging/KycIntegrationEvents.cs)
- The initiator captured at the source and carried through Kafka: `initiated_by` in the Outbox tables, see [1.3.4](#134-workflow-correlation-and-causation-identity)
- People on screen: officers appear by LAN ID (`etpar`, `olben`), never by subject ID, but every rule compares subject IDs: a LAN ID can change or be reused, a subject ID cannot. Each context keeps its own `staff_members` table (subject ID → LAN ID), filled from the officer's token and from the events that name people.

**Not yet:** four-eyes per stage (a different officer for each stage).

### 2.3 Browser and session security

#### 2.3.1 Strict Content-Security-Policy with script hashes

The Shell, every MFE BFF and the IDP send a strict Content-Security-Policy. Scripts may load only from the application's own origin. The few inline scripts that Next.js's static export needs are allowed **by their SHA-256 hash**, which each BFF computes at start-up from the exported HTML, so no `'unsafe-inline'` is needed for scripts. Styles follow the same rule: only the application's own style sheets, plus any inline `<style>` block of the export by its hash, and no `'unsafe-inline'`, so injected CSS (data theft through attribute selectors, fake overlays) is blocked too. The front ends render no `style` attributes, and each app has its own not-found page because Next.js's default one carries inline styles. The Audit web app, rendered per request, allows its styles by the request's nonce (the development server alone allows inline styles, which it injects from script). Plugins are disabled (`object-src 'none'`), forms may post only to the same origin, and frame sources are allow-listed. A report-only switch lets a new policy be observed before it is enforced.

**Where to look at:**

- .NET builder with hash computation: [ContentSecurityPolicy.cs](../src/Common/WebUtilities/Security/ContentSecurityPolicy.cs), used by [Shell Program.cs](../src/Shell/Program.cs) and [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs)
- NestJS twin: [content-security-policy.ts](../src/Microservices/CustomerKyc/BFF.Web/src/security/content-security-policy.ts)
- Report-only switches: `Security:CspReportOnly` / `KYC_BFF_CSP_REPORT_ONLY` ([ReadMe.txt](../ReadMe.txt))

- Audit web app (nonce per request): [proxy.ts](../src/Microservices/Audit/BFF.Web/proxy.ts)
- Spinner and not-found styles as classes: [AuthGuard.tsx](../src/Shell/client-app/app/components/AuthGuard.tsx), [Shell not-found.tsx](../src/Shell/client-app/app/not-found.tsx)

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

- `AddEntityFrameworkServerSideSessions`, `AddSessionCleanupBackgroundProcess` and `ExpireTimeSpan`: [Shell Program.cs](../src/Shell/Program.cs), [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs)
- Persistent Data Protection keys (DPAPI-encrypted at rest on Windows): [PersistentDataProtection.cs](../src/Common/WebUtilities/Security/PersistentDataProtection.cs)
- Session and key tables, one schema per BFF user: [EwpBffStateDb.sql](../db/EwpBffStateDb.sql)
- `session.regenerate` at sign-in: [oidc.service.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/oidc.service.ts)

Every BFF keeps its sessions in PostgreSQL, so a restart signs nobody out. The two Node.js BFFs (KYC and Audit web) store only the SHA-256 of the session ID and encrypt the session, tokens included, with AES-256-GCM under a key that is not in the database: a database reader can neither hijack nor read a session ([session-store.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/session-store.ts)).

#### 2.3.5 Open-redirect protection

After sign-in, a BFF sends the user back only to a route on an allow-list, never to an arbitrary `returnUrl`, so a crafted link cannot bounce a signed-in user to a phishing site.

**Where to look at:**

- [BffRouteCatalog.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Configuration/BffRouteCatalog.cs), `SilentLogin` in [AuthController.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Controllers/AuthController.cs)
- `safeReturnUrl` in [auth.controller.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/auth.controller.ts)

### 2.4 API and data protection

#### 2.4.1 Error responses that leak nothing

An unexpected error returns a standard problem response with a `traceId` and nothing else: no exception message, stack trace or SQL. The details are logged on the server under the same `traceId`, so support can find them. Only the service's own domain errors (for example "the case is assigned to another officer") carry a message, because those are meant for the user. There is no developer exception page.

**Where to look at:**

- [ApiExceptionHandler.cs (KYC)](../src/Microservices/CustomerKyc/API/Controllers/ApiExceptionHandler.cs), [ApiExceptionHandler.cs (CO)](../src/Microservices/CustomerOnboarding/API/API/ErrorHandling/ApiExceptionHandler.cs), [ApiExceptionHandler.cs (DM)](../src/Microservices/DocumentsManagement/API/API/ErrorHandling/ApiExceptionHandler.cs), [ApiExceptionHandler.cs (CMP)](../src/Microservices/Compliance/API/Controllers/ApiExceptionHandler.cs)

#### 2.4.2 Injection protection

All database access goes through EF Core with parameters. Raw SQL, where it is used (row locks, Outbox claiming, sequences), uses interpolated `FormattableString` queries that EF Core turns into parameters, never string concatenation. Input is validated by value objects before it reaches persistence. Output in the React and Razor front ends is encoded by the frameworks; no `innerHTML`-style rendering is used.

**Where to look at:**

- Parameterised raw SQL: [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs), [ApplicationNumberGenerator.cs](../src/Microservices/CustomerOnboarding/API/Infrastructure/Persistence/ApplicationNumberGenerator.cs)
- Validation at the edge of the domain: [1.2.2](#122-value-objects)

#### 2.4.3 Least-privilege database users

Each service connects with its own database user, which can reach only its own database and cannot change the schema (no DDL). The Customer Onboarding Outbox relay may only read and update the Outbox table: it cannot read customers. A compromised service, or a leaked connection string, therefore exposes one context's data at most, never the whole platform.

**Where to look at:**

- [EwpServiceDbUsers.sql](../db/EwpServiceDbUsers.sql) (one role per service: `ewp_customer_onboarding_api`, `ewp_customer_outbox_relay`, `ewp_kyc_api`, `ewp_documents_api`, `ewp_compliance_api`, `ewp_accounts_api`, `ewp_notifications_api`, `ewp_payments_api`, `ewp_idp`, `ewp_shell`)
- The BFFs' sessions and keys: one schema per BFF in `EwpBffStateDb`, usable only by that BFF's own role ([EwpBffStateDb.sql](../db/EwpBffStateDb.sql))

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

#### 2.4.6 Kafka authentication and per-topic ACLs

The message broker is secured like any other service. In plain terms: a program without Kafka credentials can neither write nor read any message, and even a genuine component can only touch its own topics, so it cannot impersonate another one. This is prevention, not monitoring: an unauthorised write is refused by the broker, not merely recorded. Each component connects to Kafka as its **own** user (SCRAM-SHA-512), and the broker denies everything that an ACL does not explicitly allow. A relay may write only its own context's topics. A subscriber may read only its topics, use only its consumer group, and write only its own dead-letter topic. Kafka UI gets a read-only user. Topic auto-creation is off, so a mistyped topic name fails instead of silently creating a topic. This closes a real gap: separation of duties trusts the initiator carried in `onboarding.application.submitted`, and only the Customer Onboarding relay can now publish to that topic.

**Where to look at:**

- Users, ACLs and broker settings: [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1), [kafka/README.md](../kafka/README.md)
- Client side, failing closed without credentials: [KafkaClientSecurity.cs](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs)
- Per-component Kafka user: `Kafka:SaslUsername` in each component's `appsettings.json`

**Not yet:** TLS on the broker (`SASL_SSL`). Local development uses `SASL_PLAINTEXT` on localhost.

#### 2.4.7 Rate limiting

Every .NET BFF and API limits how fast each caller may send requests (OWASP API4, unrestricted resource consumption). The budget is per caller, never one shared bucket that one user could use up for everybody: a signed-in person by subject ID (600 requests a minute, of which at most 60 changes such as payments, decisions or uploads), a machine client by client ID (3,000 a minute), and anyone not yet signed in by IP address (120 a minute). Over the limit the answer is **429 Too Many Requests** with `Retry-After`, which the screens show as a message. Health probes, SignalR hubs and the workers' internal endpoints are not limited. The IDP has its own sign-in throttling and account lockout ([2.1.3](#213-identity-provider-hardening)).

**Where to look at:**

- [RateLimiting.cs](../src/Common/WebUtilities/Security/RateLimiting.cs) (`AddEwpRateLimiting` / `UseEwpRateLimiting`), used by every BFF's and API's `Program.cs`; limits in the `RateLimiting` configuration section

#### 2.4.8 Tamper-evident audit trail

Who did what is recorded once, centrally, and cannot be quietly changed. The Audit context reads every business event from Kafka (no producer has to do anything) and appends it to a trail that keeps identifiers and people only - who initiated, who decided, which record, the outcome - plus the SHA-256 of the original message as evidence; no names or addresses. Three layers keep it honest: its database user may only insert and read; a trigger refuses updates and deletes for everybody, the owner included; and every entry's hash covers the previous entry's hash, so an edit made by someone who bypasses both still breaks the chain from that entry on. The chain is re-verified on a schedule: a break turns readiness Degraded and shows in the metrics, so an alert can fire. Auditors - and only auditors - search the trail, follow one record end to end (with its current status from the context that owns it) and verify the chain on demand; every one of those reads is itself recorded. The screens are built in the customer's pattern (Next.js light BFF → NestJS Journey API → Domain APIs), with the person carried by token exchange at every hop ([2.1.2](#212-separate-human-and-machine-identities)).

**Where to look at:**

- The trail and its chain: [AuditEntry.cs](../src/Microservices/Audit/API/Domain/AuditEntry.cs), [AuditTrailAppender.cs](../src/Microservices/Audit/API/Infrastructure/AuditTrailAppender.cs), [AuditChainVerifier.cs](../src/Microservices/Audit/API/Application/AuditChainVerifier.cs); append-only table and trigger: [EwpAuditDb.sql](../src/Microservices/Audit/API/AuditDb/EwpAuditDb.sql)
- What is kept from an event: [AuditEventMapper.cs](../src/Microservices/Audit/API/Application/AuditEventMapper.cs)
- The three tiers: [Audit web](../src/Microservices/Audit/BFF.Web/README.md), [Audit Journey API](../src/Microservices/Audit/JourneyApi/README.md), [Audit API](../src/Microservices/Audit/API/README.md)

**Not yet:** operations' "Retry release" on a payment is not an event, so it is not in the trail; the chain is not anchored outside the database (e.g. periodically to WORM storage).

### 2.5 Supply chain

#### 2.5.1 Dependency vulnerability scanning

One script checks every .NET and npm dependency of the solution against published vulnerability advisories. It fails the run when a deployed dependency has a known vulnerability at or above a chosen severity, so it can gate a CI pipeline. Dependencies are kept on patched versions (for example Next.js 16.3.8 after a critical advisory).

**Where to look at:**

- [Scan-Dependencies.ps1](../ps/build/Scan-Dependencies.ps1)

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

- The MFEs' API helpers, which send cookies and a CSRF header but no `Authorization` header: [api.ts (KYC)](../src/Microservices/CustomerKyc/BFF.Web/client-app/app/lib/api.ts), [api.ts (CO)](../src/Microservices/CustomerOnboarding/BFF.Web/client-app/app/lib/api.ts), [api.ts (CMP)](../src/Microservices/Compliance/BFF.Web/client-app/app/lib/api.ts)

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

- `AddStandardResilienceHandler` with GET-only retries in [CO BFF Program.cs](../src/Microservices/CustomerOnboarding/BFF.Web/Program.cs), [KafkaSubscriberHostedService.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs)

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