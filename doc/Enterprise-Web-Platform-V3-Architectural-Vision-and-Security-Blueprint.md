# Enterprise Web Platform V3
## Architectural Vision, Enterprise Capability Blueprint & Security Roadmap

**Document status:** Living architectural blueprint  
**Version:** 3.x  
**Domain:** Banking / Financial Services  
**Audience:** Solution Architects, Application Architects, Security Architects, Developers, DevSecOps Engineers, Platform Engineers and Technical Reviewers  
**Last updated:** September 2026

---

## 1. Purpose of Enterprise Web Platform V3

Enterprise Web Platform V3 (EWP V3) is a deliberately engineered enterprise-architecture proof of concept for a **modern banking-services platform**.

Its purpose is not merely to demonstrate that several microservices can communicate with one another. The platform is intended to demonstrate how a financial institution can design a **secure, independently deployable, observable, resilient and auditable distributed application** in which:

- business capabilities are separated into bounded contexts;
- each bounded context owns its own data;
- human authentication and machine authentication are distinct;
- authorization goes beyond simple RBAC;
- browser applications are protected by BFF security boundaries;
- microservices communicate asynchronously where appropriate;
- distributed workflows survive retries, duplicate delivery and partial failure;
- business transactions and integration events are committed atomically;
- workflow identity and causality are preserved across asynchronous boundaries;
- human users receive only the notifications that belong to their workflows;
- sensitive operations are subject to workflow-state, relationship, attribute and Separation-of-Duties controls;
- security, observability and auditability are treated as architectural concerns rather than afterthoughts;
- the platform can evolve toward cloud-native production characteristics without coupling the business domain to a particular cloud provider.

The banking domain is intentionally used because it creates realistic architectural pressures: customer onboarding, KYC, compliance, account opening, payments, document handling, approvals, audit, fraud controls, sensitive personal data and long-running workflows.

> **Important:** EWP V3 is an architectural PoC, not a production banking platform and not a declaration of regulatory compliance. A real financial institution would require additional controls, operational processes, certifications, regulatory interpretation, resilience testing and governance appropriate to its jurisdiction and risk profile.

---

# 2. Architectural Philosophy

EWP V3 follows these principles:

1. **Bounded-context ownership over shared-domain models.**
2. **Database-per-bounded-context.**
3. **Business rules belong to the domain/application layer, not the UI.**
4. **The Shell composes experiences; it does not become a business-service aggregator.**
5. **The BFF is a browser-facing security boundary.**
6. **Human identity is different from service identity.**
7. **Authorization is enforced server-side.**
8. **RBAC alone is insufficient for enterprise banking authorization.**
9. **Asynchronous integration is designed for at-least-once delivery.**
10. **Consumers are therefore idempotent.**
11. **Business state changes and outgoing messages use Transactional Outbox.**
12. **Distributed workflows propagate causality and correlation information.**
13. **Failures are expected architectural conditions, not exceptional surprises.**
14. **Retries must be bounded and combined with timeouts/circuit breakers where appropriate.**
15. **Compensation is used where a distributed transaction cannot be rolled back atomically.**
16. **Auditability is a first-class business requirement.**
17. **Sensitive information is minimized, protected and retained only as required.**
18. **Security controls are layered: identity, authorization, data, network, application, infrastructure and operational controls.**
19. **Observability must work across synchronous and asynchronous boundaries.**
20. **The platform should fail safely rather than fail open.**
21. **Every important architectural decision should have a clear ownership boundary.**
22. **The UI may guide a user, but it is never the authoritative security boundary.**

---

# 3. Current V3 Architectural Landscape

The current platform is organized around a Shell and independently deployable bounded contexts.

```text
                         +----------------------+
                         |   Duende Identity    |
                         |      Server 8        |
                         +----------+-----------+
                                    |
                         OIDC / OAuth 2.x
                                    |
                                    v
+-------------------------------------------------------------------+
|                         BSS Shell                                  |
|                                                                   |
|  Next.js Shell SPA + ASP.NET Core BFF                             |
|                                                                   |
|  - Composition                                                   |
|  - Menu/navigation                                               |
|  - Application Workspace                                         |
|  - MFE context exchange                                          |
|  - User-facing workflow notifications                            |
+-------------+----------------------+------------------------------+
              |                      |
              | iframe/MFE           | authenticated browser
              v                      v
     +----------------+       +-------------------+
     | Customer       |       | Future / other    |
     | Onboarding MFE |       | bounded contexts  |
     +-------+--------+       +-------------------+
             |
             v
     +----------------------+
     | Customer Onboarding  |
     | BFF -> API -> DB     |
     +----------+-----------+
                |
                | Kafka / Outbox
                v
        +---------------+
        | Async Workflow|
        | Subscribers   |
        +-------+-------+
                |
        +-------+--------+------------------+
        |                |                  |
        v                v                  v
   Customer KYC       Accounts          Payments
   Bounded Context    Bounded Context   Bounded Context

        +---------------------------------------+
        | Documents Management                  |
        | Document metadata / storage ownership |
        +---------------------------------------+
```

---

# 4. Bounded Contexts and Ownership

## 4.1 Customer Onboarding

**Owns:**

- Customer profile.
- Contact/address information.
- Customer lifecycle state.
- Onboarding applications.
- Onboarding workflow state.
- Customer-related workflow initiation.

**Technology direction:**

- Next.js MFE.
- ASP.NET Core BFF.
- ASP.NET Core API.
- PostgreSQL.
- EF Core.
- Transactional Outbox.
- Kafka integration.

---

## 4.2 Customer KYC

**Owns:**

- KYC cases.
- Identity verification.
- Document verification results.
- AML screening results.
- Risk assessment.
- KYC/compliance decision state.

A KYC subscriber/worker is intended to react to Customer Onboarding events and invoke the appropriate KYC business capability using a service identity.

---

## 4.3 Accounts

**Owns:**

- Account-opening applications.
- Accounts.
- Account holders.
- Account lifecycle state.

This PoC deliberately does not represent a real core-banking ledger.

---

## 4.4 Payments

**Owns:**

- Payment instructions.
- Beneficiaries.
- Payment attempts.
- Payment-processing state.

This is an architectural demonstration, not a production payment-processing platform.

---

## 4.5 Documents Management

**Owns:**

- Document metadata.
- Document lifecycle.
- Document storage integration.
- Document validation/scanning workflows.

Large binary transfers should not unnecessarily pass through unrelated business APIs.

The architectural direction is toward direct, narrowly authorized uploads and/or pre-signed object-storage URLs, so that:

- the Customer Onboarding API does not become a binary streaming bottleneck;
- document ownership remains with Documents Management;
- user context and auditability are preserved;
- large-file processing can scale independently.

---

# 5. Front-End Composition and Micro-Frontend Architecture

EWP V3 demonstrates:

- Shell-based application composition.
- Independently deployable MFEs.
- Next.js-based MFEs.
- BFF-backed browser applications.
- iframe-based MFE isolation where appropriate.
- Explicit inter-frame communication protocols.

## 5.1 BSS Application Workspace

The Shell owns the Application Workspace.

The Workspace is intentionally a **passive human-context hint**, not a business-service UI.

The Shell must not:

- call Customer APIs to resolve business entities;
- load onboarding applications itself;
- interpret business relationships;
- implement Customer Onboarding rules;
- become a cross-service business aggregator.

The Workspace transports opaque structured context.

The intended context model is:

```text
persistentContext
    |
    +-- Root business context
        e.g. Customer

currentContext
    |
    +-- Information actively relevant to the displayed MFE/page

retainedContext
    |
    +-- Previously relevant context retained for possible reuse
```

The Shell transports the context without owning its business meaning.

---

# 6. BSS MFE Context and Navigation Protocol

The V3 Shell/MFE protocol includes:

- `BSS_MFE_READY`
- `BSS_CONTEXT_HANDOFF`
- `BSS_CONTEXT_UPDATE`
- `BSS_NAVIGATION_REQUEST`
- `BSS_NAVIGATION_RESPONSE`

The navigation protocol allows an MFE to prevent navigation when it has unsaved work.

The intended sequence is:

```text
Shell
  |
  | BSS_NAVIGATION_REQUEST
  v
Current MFE
  |
  +-- clean --------------------> BSS_NAVIGATION_RESPONSE(allowed=true)
  |
  +-- dirty --> user confirmation
                    |
                    +-- Leave --> allowed=true
                    |
                    +-- Stay --> allowed=false
```

This provides a clean separation between:

- Shell navigation responsibility.
- MFE business/form state.
- Human confirmation.

---

# 7. Identity and Access Management

## 7.1 Identity Provider

The platform uses **Duende IdentityServer 8.0.8** on ASP.NET Core 10.

The IDP is responsible for:

- authentication;
- OIDC;
- OAuth 2.x;
- client registration;
- scopes;
- API resources;
- role claims;
- stable opaque subject identifiers;
- refresh-token support where configured;
- consent where appropriate.

Identity data remains separate from business microservice databases.

---

## 7.2 Human Authentication

The target human authentication architecture is:

```text
Browser
   |
   v
BFF
   |
   v
IdentityServer
   |
   +-- Authorization Code
   +-- PKCE
   |
   v
Authenticated User Session
```

The platform should continue to avoid putting long-lived bearer tokens unnecessarily into browser JavaScript.

---

## 7.3 Machine Authentication

Service-to-service calls use separate machine identities.

Typical examples:

```text
CustomerKycSubscriber
DocumentsManagementClient
AccountsWorkflowWorker
PaymentsWorkflowWorker
```

M2M authentication should normally use OAuth 2.0 Client Credentials or another appropriate workload-identity mechanism.

A service identity **must not be confused with the human who initiated a workflow**.

---

# 8. Human Initiator and Workflow Identity

A critical V3 architectural capability is preserving the identity of the human who originally initiated a long-running workflow.

The CO API now persists:

```text
outbox_messages.initiated_by
```

The intended semantic distinction is:

```text
initiated_by
    = human/user identity that originated the workflow

service identity
    = machine identity executing a subsequent asynchronous step
```

Example:

```text
Susan
  |
  | starts onboarding
  v
Customer Onboarding API
  |
  +-- business transaction
  |
  +-- outbox.initiated_by = Susan
  |
  v
Kafka
  |
  v
KYC Subscriber
  |
  +-- M2M identity = KYC Subscriber
  |
  +-- initiated_by = Susan
  |
  v
KYC API
```

This distinction is essential for:

- audit;
- workflow ownership;
- user-specific notifications;
- downstream event correlation;
- operational investigation;
- future workflow reassignment.

The outbox publisher should **propagate persisted initiator metadata**, not attempt to discover the user after the transaction has completed.

---

# 9. Distributed Workflow Architecture

The target workflow model is a combination of:

- event-driven architecture;
- choreography where appropriate;
- Saga orchestration/choreography for long-running business workflows;
- compensating actions;
- Transactional Outbox;
- Inbox/idempotent consumer processing;
- correlation and causation propagation.

Example:

```text
Customer Onboarding
        |
        | customer.created
        v
Customer KYC
        |
        | kyc.completed
        v
Compliance
        |
        | compliance.completed
        v
Accounts
        |
        | account.opened
        v
Workflow Completion
        |
        v
SignalR Notification
```

The exact workflow topology may evolve between choreography and explicit orchestration depending on business complexity.

---

# 10. Transactional Outbox

The Transactional Outbox is a core architectural capability.

The business transaction and its outgoing integration event must be committed atomically:

```text
BEGIN TRANSACTION

    Business state change

    INSERT outbox_messages (...)

COMMIT
```

Only after commit does the publisher deliver the message to Kafka.

The outbox is therefore the durable bridge between:

```text
Database transaction
        |
        v
Asynchronous messaging
```

---

# 11. At-Least-Once Delivery

The platform assumes that message delivery can occur more than once.

For example:

```text
Publisher
   |
   | Kafka publish succeeds
   v
Kafka
   |
   X process crashes before outbox update
   |
   v
Publisher restarts
   |
   | publishes again
   v
Kafka
```

Therefore:

> **Consumers must be idempotent.**

Exactly-once business behavior must be achieved through idempotent business processing, unique constraints, Inbox/Processed Message records and appropriate transaction boundaries rather than assuming exactly-once network delivery.

---

# 12. Inbox / Idempotent Consumer Pattern

Consumers should record processed message identity.

Typical model:

```text
message_id
consumer
processed_at
```

with an appropriate uniqueness constraint such as:

```text
UNIQUE(message_id, consumer)
```

Processing should conceptually be:

```text
Receive message
     |
     v
Check Inbox
     |
     +-- already processed --> ACK / ignore
     |
     +-- new
          |
          v
      Begin transaction
          |
          +-- business change
          +-- next outbox event
          +-- inbox record
          |
          v
        Commit
          |
          v
       ACK Kafka
```

This is a major reliability boundary for the asynchronous architecture.

---

# 13. Event Envelope and Workflow Metadata

The target integration-event envelope should provide sufficient information to understand the event without querying the originating service's database.

Recommended metadata:

```text
MessageId
EventType
Source
OccurredAt
WorkflowId
CorrelationId
CausationId
TraceId
InitiatedByUserId
Payload
```

Where useful, additional metadata may include:

```text
SchemaVersion
TenantId
Environment
ProducerVersion
SecurityClassification
```

The exact metadata set should remain intentionally small and stable.

---

# 14. Correlation, Causation and Traceability

These identifiers have different meanings and should not be conflated.

### Correlation ID

Groups messages belonging to the same business interaction or request chain.

### Causation ID

Identifies the event/message that caused the current event.

### Workflow / Saga ID

Identifies the long-running business process.

### Trace ID

Identifies a distributed technical execution trace.

### Message ID

Uniquely identifies an individual message.

### Initiated By User ID

Identifies the human originator of the workflow.

Conceptually:

```text
Susan
  |
  +-- WorkflowId = W123
       |
       +-- CorrelationId = C456
       |
       +-- CustomerCreated
              MessageId = M001
              CausationId = null
              InitiatedBy = Susan
                    |
                    v
              KYCRequested
              MessageId = M002
              CausationId = M001
              InitiatedBy = Susan
                    |
                    v
              KycCompleted
              MessageId = M003
              CausationId = M002
              InitiatedBy = Susan
```

---

# 15. Subscriber / Worker Architecture

The Customer KYC Subscriber is intended to become a representative enterprise workflow worker.

Target responsibilities:

1. Consume a specific Kafka event/topic.
2. Deserialize and validate the event envelope.
3. Check Inbox/idempotency.
4. Extract workflow metadata.
5. Acquire an M2M access token.
6. Call the appropriate bounded-context API.
7. Apply retries/timeouts/circuit breaking as appropriate.
8. Execute the business operation.
9. Persist the resulting business state and next outbox event atomically.
10. Record successful consumption.
11. Acknowledge the Kafka message only after successful processing.
12. Emit structured logs and tracing information.
13. Route unrecoverable messages to a dead-letter/recovery mechanism.

The worker must not impersonate the human initiator merely because it carries the initiator's identity as workflow metadata.

---

# 16. SignalR Workflow Notifications

The Shell is intended to provide human-facing real-time workflow notifications.

The target architecture is:

```text
Kafka
  |
  v
Notification Subscriber
  |
  +-- reads InitiatedByUserId
  |
  v
SignalR Hub
  |
  +-- authenticated user identity
  |
  v
Specific user's browser
```

Example:

```text
Susan starts Customer 1
Margaret starts Customer 2

Customer 1 events
    -> Susan's SignalR channel

Customer 2 events
    -> Margaret's SignalR channel
```

A broadcast-to-all-users model is explicitly not the intended design for workflow-specific notifications.

Future notification authorization may additionally support:

- workflow initiator;
- assigned officer;
- supervisor;
- authorized operations team;
- escalation recipient.

---

# 17. Authorization Model

The target authorization pipeline is:

```text
Authenticated User
        |
        v
      R-BAC
        |
        v
      A-BAC
        |
        v
     Re-BAC
        |
        v
 Workflow State
        |
        v
 Separation of Duties
        |
        v
Authorization Decision
```

## RBAC

Role-based responsibilities:

- Customer
- Customer Service Agent
- KYC Officer
- Compliance Officer
- Account Officer
- Payments Officer
- Operations Administrator
- Auditor
- Platform Administrator

## ABAC

Potential attributes include:

- branch;
- department;
- employment type;
- clearance level;
- risk classification;
- transaction amount;
- device/session trust;
- geographic/environmental restrictions;
- business hours;
- customer segment.

## ReBAC

Potential relationships include:

- customer assigned to branch;
- officer assigned to case;
- employee belongs to department;
- account belongs to customer;
- KYC case belongs to onboarding application;
- supervisor manages officer;
- workflow task assigned to user/team.

## Workflow-State Authorization

An operation may be denied because the resource is in the wrong business state even when the user has the appropriate role.

Example:

```text
KYC approval
    requires:
        KYC Officer
        +
        KYC case in reviewable state
        +
        correct organizational scope
        +
        SoD satisfied
```

---

# 18. Separation of Duties

Banking workflows must be designed to prevent incompatible actions from being performed by the same person where policy requires independence.

Examples:

```text
Application creator
        !=
KYC approver

KYC approver
        !=
Compliance approver

Payment initiator
        !=
Payment approver
```

The exact SoD matrix should be represented as business policy rather than scattered across UI code.

---

# 19. Auditability

The platform should support a durable audit trail for significant actions.

An audit record should be able to answer:

- Who performed the action?
- What action was performed?
- On which business resource?
- When?
- From which application/service?
- Under which workflow?
- What was the previous state?
- What was the resulting state?
- What authorization decision permitted it?
- Which approval or SoD rule was evaluated?
- Which message or request caused the action?

Audit records should be:

- tamper-resistant;
- access-controlled;
- time-synchronized;
- searchable;
- retained according to policy;
- separated from ordinary application logs where appropriate.

---

# 20. Data Protection and Privacy

A banking platform should apply data minimization and privacy-by-design.

Target capabilities include:

- data classification;
- encryption in transit;
- encryption at rest;
- key management;
- secrets management;
- field-level protection/tokenization where justified;
- masking of sensitive values in logs;
- PII-aware telemetry;
- controlled data export;
- retention policies;
- secure deletion where legally permissible;
- purpose limitation;
- least-privilege access;
- controlled operational support access.

Passwords, access tokens, secrets, private keys and sensitive personal data must never be written to ordinary application logs.

---

# 21. Document Security

For KYC/customer documents, target controls include:

- short-lived upload authorization;
- narrow document/application scope;
- object-level authorization;
- malware/antivirus scanning;
- content-type validation;
- file signature validation;
- file-size limits;
- filename normalization;
- storage encryption;
- immutable or controlled document versions;
- document retention policy;
- secure download authorization;
- download auditing;
- quarantine for suspicious files.

Where cloud object storage is used, the preferred pattern is:

```text
MFE
 |
 | request narrowly scoped upload authorization
 v
Documents Management
 |
 | pre-signed URL / scoped token
 v
Object Storage
 |
 | asynchronous validation/scanning
 v
Document becomes trusted/available
```

---

# 22. API Security

All APIs should follow a defense-in-depth model.

Target controls include:

- OAuth/OIDC;
- audience validation;
- scope validation;
- issuer validation;
- token lifetime controls;
- least-privilege scopes;
- server-side authorization;
- object-level authorization;
- input validation;
- output encoding;
- request-size limits;
- rate limiting;
- anti-forgery protection where applicable;
- secure HTTP headers;
- CORS restrictions;
- content-type validation;
- replay protection where appropriate;
- idempotency keys for suitable commands;
- API versioning;
- consistent error handling;
- secure error responses.

API endpoints should never rely on menu visibility or front-end checks as the security boundary.

---

# 23. Modern OAuth / Financial-Grade Security Direction

The platform currently uses Authorization Code + PKCE.

The security roadmap should remain aligned with current OAuth security guidance, including:

- exact redirect URI validation;
- PKCE;
- avoiding insecure implicit-style flows;
- protection against authorization-code injection;
- careful refresh-token handling;
- sender-constrained tokens where justified;
- strong TLS;
- secure token storage;
- narrowly scoped access tokens;
- short-lived access tokens where appropriate;
- key rotation;
- metadata discovery where appropriate;
- secure client authentication.

For higher-risk financial APIs, the architecture should evaluate **FAPI 2.0** and related OpenID Foundation profiles rather than assuming ordinary OAuth configuration is sufficient.

---

# 24. BFF Security Model

The BFF is a key browser security boundary.

Target characteristics:

- secure session cookies;
- HttpOnly;
- Secure;
- appropriate SameSite configuration;
- anti-forgery protection;
- session fixation protection;
- session renewal;
- logout propagation;
- strict origin validation;
- controlled downstream token handling;
- no unnecessary exposure of access tokens to JavaScript;
- authorization checks on server-side endpoints;
- appropriate CSRF protection.

The BFF should not become a generic unrestricted proxy.

Each downstream operation should have explicit authorization and purpose.

---

# 25. Cross-Origin and Browser Security

Target browser security controls include:

- strict Content Security Policy;
- frame-ancestors policy;
- controlled iframe origins;
- explicit postMessage origin validation;
- explicit postMessage source validation;
- X-Content-Type-Options;
- Referrer-Policy;
- Permissions-Policy;
- HSTS in deployed HTTPS environments;
- secure cookie configuration;
- controlled CORS;
- protection against clickjacking;
- protection against open redirects.

The existing BSS MFE protocol should continue to validate both:

```text
event.origin
event.source
```

before accepting inter-frame messages.

---

# 26. Input and Output Security

All externally supplied data should be treated as untrusted.

Target controls:

- validation at API boundaries;
- domain invariant validation;
- length limits;
- range validation;
- enum validation;
- canonicalization;
- safe parsing;
- parameterized database access;
- output encoding;
- HTML sanitization where HTML is accepted;
- SSRF protection;
- path traversal protection;
- command injection protection;
- deserialization restrictions;
- secure URL validation.

---

# 27. Secrets and Cryptographic Key Management

Production architecture should not depend on secrets stored in source code, Git repositories or plaintext configuration.

Target design:

```text
Application
    |
    v
Managed Identity / Workload Identity
    |
    v
Secrets / Key Management Service
    |
    +-- database credentials
    +-- OAuth client secrets where unavoidable
    +-- signing keys
    +-- encryption keys
    +-- external-service credentials
```

Capabilities should include:

- rotation;
- versioning;
- expiration;
- access auditing;
- least privilege;
- separation of development/test/production keys.

---

# 28. Cryptography and Key Rotation

Target cryptographic capabilities include:

- TLS 1.2+ with modern cipher configuration;
- preferably TLS 1.3 where supported and appropriate;
- strong asymmetric signing algorithms;
- key rotation;
- certificate lifecycle automation;
- separation of signing and encryption keys;
- hardware-backed key protection where required;
- cryptographic agility;
- no hard-coded cryptographic secrets.

---

# 29. Service-to-Service Security

M2M communication should provide:

- strong client authentication;
- least-privilege scopes;
- audience restrictions;
- certificate/mTLS where justified;
- workload identity where available;
- network-level segmentation;
- explicit authorization;
- short-lived tokens;
- credential rotation;
- service identity auditing.

A successful M2M authentication does not automatically mean that the caller is authorized to perform every business operation.

---

# 30. Zero-Trust Direction

The platform should evolve toward a zero-trust model:

```text
Never implicitly trust
        |
        v
Authenticate
        |
        v
Authorize
        |
        v
Validate context
        |
        v
Apply least privilege
        |
        v
Continuously observe
```

Network location alone must not be considered sufficient proof of authorization.

---

# 31. Resilience Engineering

Target resilience patterns include:

- bounded retries;
- exponential backoff;
- jitter;
- timeout;
- circuit breaker;
- bulkhead isolation;
- rate limiting;
- load shedding;
- graceful degradation;
- idempotency;
- dead-letter queues;
- poison-message handling;
- compensation;
- health checks;
- readiness/liveness separation;
- dependency isolation.

Retries must be used carefully.

A retry must not turn:

```text
one failed payment
```

into:

```text
multiple payment attempts
```

without explicit idempotency protection.

---

# 32. Kafka Architecture

Kafka is the event backbone.

Target capabilities include:

- explicit topic naming conventions;
- event schema versioning;
- consumer groups;
- partition strategy;
- key selection;
- ordering requirements documented per event;
- retention policies;
- retry topics;
- dead-letter topics;
- poison-message handling;
- producer/consumer observability;
- message-size limits;
- ACLs;
- encryption in transit;
- authentication;
- authorization;
- consumer lag monitoring.

Event contracts should evolve compatibly.

Breaking changes should require a deliberate schema/versioning strategy.

---

# 33. Event Contract Governance

Each integration event should have:

- stable event name;
- version;
- producer ownership;
- consumer expectations;
- schema;
- compatibility policy;
- security classification;
- PII classification;
- retention requirements;
- ordering requirements;
- idempotency requirements.

The event payload should contain enough information for the consumer to perform its intended task without requiring synchronous calls merely to reconstruct basic event context.

---

# 34. Observability

The platform should provide three complementary observability pillars:

```text
Logs
Metrics
Traces
```

Target implementation:

- OpenTelemetry;
- distributed tracing;
- trace context propagation;
- structured logging;
- metrics;
- Kafka consumer lag;
- API latency;
- error rates;
- dependency latency;
- database performance;
- circuit-breaker state;
- retry counts;
- workflow duration;
- workflow failure rate.

Sensitive data must be excluded or masked.

---

# 35. Security Monitoring and SIEM Integration

A real banking deployment should feed security-relevant events into centralized security monitoring.

Examples:

- authentication failures;
- unusual login behavior;
- authorization failures;
- privilege changes;
- role changes;
- service credential misuse;
- suspicious API access;
- large-volume data access;
- document download anomalies;
- payment anomalies;
- repeated workflow failures;
- administrative operations.

The architecture should support integration with:

- SIEM;
- SOC;
- SOAR;
- threat-intelligence feeds;
- fraud-monitoring systems.

---

# 36. Fraud and Risk Controls

A banking platform should be capable of integrating with fraud/risk decision systems.

Examples:

```text
Transaction
   |
   v
Risk signals
   |
   +-- customer profile
   +-- device
   +-- location
   +-- transaction history
   +-- velocity
   +-- beneficiary history
   +-- behavioral signals
   |
   v
Risk decision
   |
   +-- allow
   +-- challenge
   +-- hold
   +-- reject
   +-- manual review
```

The PoC can simulate these decisions without implementing a real production fraud engine.

---

# 37. KYC / AML Architecture

The platform should demonstrate that KYC and compliance are independent bounded responsibilities.

Target workflow:

```text
Customer Onboarding
        |
        v
Identity Verification
        |
        v
Document Verification
        |
        v
AML Screening
        |
        v
Risk Assessment
        |
        v
Compliance Decision
        |
        v
Account Opening
```

Each step should have:

- explicit state;
- owner;
- authorization policy;
- audit event;
- retry/failure behavior;
- correlation metadata;
- compensation/escalation strategy where applicable.

---

# 38. Business State Machines

Important financial workflows should use explicit state models.

Example:

```text
DRAFT
  |
  v
SUBMITTED
  |
  v
KYC_IN_PROGRESS
  |
  v
KYC_COMPLETED
  |
  v
COMPLIANCE_IN_PROGRESS
  |
  v
COMPLIANCE_COMPLETED
  |
  v
ACCOUNT_OPENING_IN_PROGRESS
  |
  v
COMPLETED
```

Exceptional states may include:

```text
REJECTED
CANCELLED
COMPENSATING
COMPENSATION_FAILED
```

Invalid transitions must be rejected by the domain/application layer.

---

# 39. Concurrency and Consistency

Target controls include:

- optimistic concurrency;
- version columns;
- unique constraints;
- transaction boundaries;
- idempotency keys;
- duplicate detection;
- deterministic state transitions;
- safe retry behavior.

Distributed systems should prefer explicit consistency boundaries over pretending that a single ACID transaction spans multiple databases.

---

# 40. Database Architecture

Each bounded context owns its database.

Rules:

- no cross-service database access;
- no shared business tables;
- no foreign keys across databases;
- integration through APIs/events;
- schema changes through migrations/versioned scripts;
- least-privilege DB accounts;
- encrypted database connections;
- database backups;
- restore testing;
- point-in-time recovery where required;
- audit access to privileged database operations.

---

# 41. Database Security

Target database controls include:

- separate credentials per service;
- minimum required privileges;
- no application use of superuser accounts;
- encryption at rest;
- TLS connections;
- credential rotation;
- connection limits;
- query timeouts;
- auditing;
- backup encryption;
- tested restoration;
- protected administrative access.

---

# 42. Secure File and Object Storage

For customer documents:

- encryption at rest;
- short-lived access URLs;
- object-level authorization;
- private buckets/containers;
- malware scanning;
- immutable retention where required;
- versioning;
- access logging;
- lifecycle policies;
- data classification;
- geographic/data-residency controls where required.

---

# 43. Supply-Chain Security

Modern enterprise security must include the software supply chain.

Target capabilities:

- dependency vulnerability scanning;
- Software Composition Analysis (SCA);
- SBOM generation;
- signed artifacts;
- provenance/attestation;
- trusted package feeds;
- dependency pinning;
- automated patching;
- container image scanning;
- base-image lifecycle management;
- secret scanning;
- static application security testing;
- dynamic application security testing;
- infrastructure-as-code scanning.

---

# 44. Secure SDLC / DevSecOps

Security should be integrated into the delivery pipeline.

Target pipeline:

```text
Developer
   |
   v
Pre-commit checks
   |
   v
Build
   |
   +-- SAST
   +-- SCA
   +-- Secret scan
   +-- SBOM
   +-- IaC scan
   |
   v
Unit tests
   |
   v
Integration tests
   |
   v
Security tests
   |
   v
Container/image scan
   |
   v
Deploy to controlled environment
   |
   v
DAST / API security testing
   |
   v
Approval / promotion
```

Security findings should have defined severity, ownership and remediation SLAs.

---

# 45. Infrastructure and Platform Security

Target infrastructure controls include:

- network segmentation;
- private subnets;
- firewall/security-group controls;
- API gateway/WAF where appropriate;
- workload identity;
- container hardening;
- Kubernetes RBAC where applicable;
- pod/container security controls;
- resource limits;
- network policies;
- immutable deployments;
- infrastructure-as-code;
- configuration drift detection;
- centralized secrets;
- centralized logging.

---

# 46. API Gateway / Edge Protection

For production internet-facing systems, an edge layer may provide:

- TLS termination;
- WAF;
- DDoS protection;
- rate limiting;
- bot protection;
- request filtering;
- API authentication integration;
- routing;
- versioning;
- observability.

The gateway should complement, not replace, authorization in the application.

---

# 47. Rate Limiting and Abuse Protection

Rate limiting should exist at appropriate boundaries:

- login;
- token endpoints;
- public APIs;
- customer APIs;
- document uploads;
- payment initiation;
- expensive queries;
- password/account recovery;
- administrative APIs.

Limits should be based on appropriate identities and dimensions, such as:

- user;
- client;
- IP;
- tenant;
- API key;
- endpoint;
- risk category.

---

# 48. Availability and Business Continuity

A real banking platform must address:

- high availability;
- fault domains;
- database replication;
- backup;
- point-in-time recovery;
- disaster recovery;
- RPO;
- RTO;
- multi-zone deployment;
- potentially multi-region deployment;
- dependency failure;
- degraded-mode operation;
- operational runbooks;
- regular recovery testing.

The PoC can model these concerns even when local infrastructure does not implement full HA.

---

# 49. Operational Resilience

The platform should be designed so that operational teams can:

- identify a failed workflow;
- identify its initiator;
- identify the last successful step;
- identify the failed message;
- replay safely;
- inspect Inbox state;
- inspect Outbox state;
- inspect Saga state;
- trigger controlled compensation;
- disable a problematic consumer;
- recover from poison messages;
- correlate application logs and Kafka events.

Operational recovery must preserve auditability.

---

# 50. Secure Error Handling

External responses should avoid revealing:

- stack traces;
- SQL details;
- internal hostnames;
- secrets;
- token contents;
- infrastructure topology;
- internal exception messages;
- sensitive business information.

Internally, errors should remain richly diagnosable through secure structured telemetry.

---

# 51. Configuration Management

Configuration should be:

- environment-specific;
- externalized;
- validated at startup;
- version controlled where non-sensitive;
- secret-free in source control;
- auditable;
- centrally managed where appropriate.

Configuration should distinguish:

```text
Development
Test
UAT
Production
```

Production credentials and cryptographic keys must never be copied into development environments.

---

# 52. Testing Strategy

The platform should demonstrate multiple test levels.

## Unit Tests

- domain rules;
- state transitions;
- authorization policies;
- mapping;
- idempotency rules.

## Integration Tests

- database;
- Kafka;
- Outbox;
- Inbox;
- IdentityServer;
- downstream APIs.

## Contract Tests

- event schemas;
- API contracts;
- consumer/producer compatibility.

## End-to-End Tests

- user authentication;
- MFE navigation;
- onboarding;
- KYC workflow;
- account workflow;
- notification delivery.

## Security Tests

- authentication;
- authorization;
- IDOR/BOLA;
- CSRF;
- XSS;
- SSRF;
- injection;
- token validation;
- privilege escalation;
- session security.

---

# 53. Chaos and Failure Testing

The distributed workflow should deliberately simulate:

- Kafka unavailable;
- duplicate Kafka delivery;
- consumer crash;
- API timeout;
- transient HTTP failure;
- permanent HTTP failure;
- database failure;
- downstream service unavailable;
- stale workflow state;
- duplicate command;
- compensation failure.

The objective is to demonstrate that the architecture remains safe and diagnosable under failure.

---

# 54. Architectural Status

The following status classification is used in this document.

| Status | Meaning |
|---|---|
| **Present** | Implemented or demonstrably represented in the current V3 codebase |
| **In Progress** | Partially implemented and actively being developed |
| **Planned** | Explicitly discussed and intended for V3 |
| **Target** | Enterprise capability that the architecture should eventually demonstrate |
| **Production Concern** | Important for a real bank but intentionally beyond the PoC's immediate scope |

---

# 55. V3 Capability Matrix

| Capability | Status |
|---|---|
| Microservices / bounded contexts | **Present** |
| Database per bounded context | **Present** |
| Clean/layered architecture | **Present** |
| Next.js MFEs | **Present** |
| Shell composition application | **Present** |
| Shell BFF | **Present** |
| MFE/BFF security boundary | **Present** |
| Duende IdentityServer 8 | **Present** |
| OIDC | **Present** |
| OAuth 2.x | **Present** |
| Authorization Code + PKCE | **Present** |
| Role-based authorization | **Present** |
| ABAC model | **Planned / In Progress** |
| ReBAC model | **Planned / In Progress** |
| Separation of Duties | **Planned / In Progress** |
| Workflow-state authorization | **Planned / In Progress** |
| Application Workspace | **Present** |
| Opaque MFE context exchange | **Present** |
| Navigation/unsaved-data protocol | **Present** |
| Transactional Outbox | **Present** |
| `initiated_by` on CO outbox messages | **Present** |
| Kafka event backbone | **Present** |
| At-least-once delivery model | **Present** |
| Inbox/idempotent consumer pattern | **In Progress** |
| Customer KYC subscriber | **In Progress** |
| M2M client-credentials workflow calls | **Planned / In Progress** |
| Retry / timeout / circuit breaker | **In Progress** |
| Saga/choreography | **Planned / In Progress** |
| Compensation | **Planned / In Progress** |
| Correlation ID | **In Progress** |
| Causation ID | **In Progress** |
| Workflow/Saga ID | **Planned / In Progress** |
| Initiator propagation through Kafka | **In Progress** |
| User-specific SignalR notifications | **Planned** |
| Workflow notification authorization | **Planned** |
| Centralized audit trail | **Planned** |
| OpenTelemetry | **Planned** |
| Distributed tracing | **Planned** |
| Kafka DLQ/recovery strategy | **Planned** |
| Event schema governance | **Target** |
| Direct/pre-signed document upload | **Planned** |
| Malware scanning | **Target** |
| Secrets management | **Target** |
| Key rotation | **Target** |
| SBOM | **Target** |
| SAST/SCA/DAST | **Target** |
| Artifact signing/provenance | **Target** |
| WAF/API edge protection | **Production Concern** |
| DDoS protection | **Production Concern** |
| SIEM/SOC integration | **Production Concern** |
| Fraud/risk engine integration | **Target** |
| High availability | **Production Concern** |
| Disaster recovery | **Production Concern** |
| Multi-region resilience | **Production Concern** |
| Regulatory compliance evidence | **Production Concern** |

---

# 56. What V3 Should Demonstrate as a Complete Reference Workflow

The principal demonstration should eventually look like this:

```text
1. Human authenticates
        |
        v
2. Shell establishes secure session
        |
        v
3. Customer Service Agent selects Customer
        |
        v
4. Shell hands opaque context to Customer Onboarding MFE
        |
        v
5. Customer Onboarding creates/submits application
        |
        +--> business transaction
        |
        +--> outbox record
              initiated_by = human user
              workflow_id
              correlation_id
              causation_id
        |
        v
6. Outbox Publisher
        |
        v
7. Kafka
        |
        v
8. KYC Subscriber
        |
        +--> Inbox/idempotency
        |
        +--> M2M token
        |
        +--> KYC API
        |
        +--> business transaction
        |
        +--> next outbox event
        |
        v
9. Kafka
        |
        v
10. Compliance / Accounts workflow
        |
        v
11. Final workflow event
        |
        v
12. Notification Subscriber
        |
        +--> identifies InitiatedByUserId
        |
        v
13. SignalR
        |
        v
14. Only the originating human sees the notification
```

This workflow demonstrates far more than microservice communication. It demonstrates **identity propagation, asynchronous consistency, idempotency, workflow state, machine authentication, human attribution, resilience and real-time user feedback**.

---

# 57. Security Architecture Objectives

The completed V3 architecture should be able to demonstrate the following security properties:

### Authentication

> Can the platform reliably establish who is acting?

### Authorization

> Is the authenticated principal allowed to perform this exact operation on this exact resource in this exact state?

### Accountability

> Can the institution prove who performed the operation?

### Confidentiality

> Can unauthorized parties obtain sensitive data?

### Integrity

> Can unauthorized parties modify business state?

### Availability

> Can failures be isolated without corrupting workflows?

### Non-repudiation / auditability

> Can important business actions be reconstructed after the fact?

### Resilience

> Can duplicate delivery, retries and partial failures occur without producing unsafe business outcomes?

---

# 58. Enterprise Security Principles to Preserve

The V3 implementation should continually apply:

```text
Zero Trust
Least Privilege
Defense in Depth
Secure by Default
Fail Securely
Verify Explicitly
Minimize Data
Minimize Blast Radius
Separate Duties
Separate Identities
Separate Ownership
Assume Failure
Assume Duplicate Delivery
Audit Important Actions
Never Trust the UI
```

---

# 59. Contemporary Security and Architecture Reference Baseline

The following external standards and guidance are useful reference points for the V3 security roadmap.

## OWASP ASVS

The OWASP Application Security Verification Standard provides a structured basis for verifying web-application security controls. The current stable ASVS line is 5.0.0.

https://owasp.org/www-project-application-security-verification-standard/

## OAuth 2.0 Security BCP — RFC 9700

RFC 9700, published by the IETF in January 2025, updates OAuth security guidance and deprecates weaker approaches.

https://www.rfc-editor.org/rfc/rfc9700

## OpenID FAPI 2.0

FAPI 2.0 provides a high-security OAuth profile intended for demanding API ecosystems including financial and open-banking scenarios.

https://openid.net/wg/fapi/specifications/

## NIST Cybersecurity Framework 2.0

NIST CSF 2.0 provides a broad framework for cybersecurity risk management and is useful as an organizational/security-control reference.

https://www.nist.gov/cyberframework

## NIST Secure Software Development Framework

NIST SSDF provides a common framework for integrating secure software development practices into an SDLC.

https://csrc.nist.gov/pubs/sp/800/218/final

## Reserve Bank of India — IT Governance, Risk, Controls and Assurance

For an India-based banking context, the RBI's Master Direction on Information Technology Governance, Risk, Controls and Assurance Practices is an important reference for governance, IT risk, controls, assurance, business continuity and information-systems audit.

https://www.rbi.org.in/

## RBI — Digital Payment Security Controls

Where payment functionality is within scope, RBI's Digital Payment Security Controls are another important reference point for authentication, application security lifecycle, fraud-risk management and payment security controls.

https://www.rbi.org.in/

> These references are architectural/security baselines, not a statement that EWP V3 currently complies with them.

---

# 60. What This PoC Deliberately Does Not Claim

EWP V3 does **not** claim to be:

- a complete core-banking system;
- a production payment switch;
- a production AML engine;
- a production fraud-detection platform;
- a certified banking application;
- a complete RBI-compliant implementation;
- a replacement for a bank's SOC/SIEM;
- a complete disaster-recovery implementation;
- a complete regulatory/audit implementation;
- a production-grade cloud platform.

Instead, it provides a technically realistic architecture in which these concerns can be demonstrated and evolved.

---

# 61. Architectural Evolution Roadmap

## Phase 1 — Foundation

**Goal:** establish reliable event identity and workflow metadata.

- [x] Transactional Outbox
- [x] Persist human initiator
- [ ] Standard event envelope
- [ ] Correlation ID
- [ ] Causation ID
- [ ] Workflow/Saga ID
- [ ] Initiator propagation through Kafka

---

## Phase 2 — Reliable Subscribers

**Goal:** build a production-style asynchronous worker.

- [ ] Customer KYC subscriber
- [ ] Inbox/idempotency
- [ ] M2M client credentials
- [ ] API authorization
- [ ] Retry
- [ ] Timeout
- [ ] Circuit breaker
- [ ] DLQ/recovery
- [ ] Structured tracing
- [ ] Transactional business update + next outbox event

---

## Phase 3 — Distributed Workflow

**Goal:** demonstrate a complete Saga.

- [ ] Customer onboarding
- [ ] KYC
- [ ] Compliance
- [ ] Account opening
- [ ] Workflow state
- [ ] Compensation
- [ ] Failure recovery
- [ ] Workflow audit history

---

## Phase 4 — Human Workflow Feedback

**Goal:** connect asynchronous backend progress to the correct human.

- [ ] SignalR hub
- [ ] Authenticated user connection
- [ ] Initiator-specific routing
- [ ] Workflow completion notification
- [ ] Failure/escalation notification
- [ ] Notification authorization

---

## Phase 5 — Enterprise Security

**Goal:** demonstrate defense-in-depth.

- [ ] ABAC
- [ ] ReBAC
- [ ] SoD
- [ ] Fine-grained API authorization
- [ ] Audit trail
- [ ] Security event model
- [ ] PII-aware logging
- [ ] Secrets management
- [ ] Key rotation
- [ ] CSP/security headers
- [ ] Rate limiting
- [ ] Advanced token protection where justified

---

## Phase 6 — Secure Delivery

**Goal:** make security part of the SDLC.

- [ ] SAST
- [ ] SCA
- [ ] Secret scanning
- [ ] SBOM
- [ ] Container scanning
- [ ] IaC scanning
- [ ] DAST
- [ ] API security testing
- [ ] Artifact signing
- [ ] Supply-chain provenance

---

## Phase 7 — Operational Resilience

**Goal:** demonstrate enterprise-operational maturity.

- [ ] OpenTelemetry
- [ ] Centralized logs
- [ ] Metrics
- [ ] Distributed tracing
- [ ] Kafka monitoring
- [ ] Workflow dashboards
- [ ] SIEM integration
- [ ] Alerting
- [ ] Disaster recovery
- [ ] Backup/restore testing
- [ ] Chaos/failure testing
- [ ] Operational runbooks

---

# 62. Definition of Architectural Success

EWP V3 should be considered successful when a reviewer can trace a single business action from:

```text
Human
  |
  v
Authentication
  |
  v
Authorization
  |
  v
MFE
  |
  v
BFF
  |
  v
Domain API
  |
  v
Database Transaction
  |
  +--> Outbox
          |
          v
        Kafka
          |
          v
      Subscriber
          |
          +--> M2M identity
          |
          +--> Human initiator identity
          |
          v
      Downstream API
          |
          v
      New business state
          |
          +--> New Outbox event
          |
          v
        Kafka
          |
          v
      Notification
          |
          v
   Correct human user
```

while simultaneously demonstrating:

- no cross-service database coupling;
- no trust in browser-only authorization;
- no loss of human workflow attribution;
- safe duplicate message processing;
- bounded failure/retry behavior;
- auditable business decisions;
- secure machine-to-machine communication;
- appropriate separation of duties;
- traceability across synchronous and asynchronous boundaries.

That is the central architectural purpose of **Enterprise Web Platform V3**.

---

# 63. Living-Document Rule

This document is intentionally a living architectural blueprint.

When a major architectural capability is introduced:

1. Update its status in the capability matrix.
2. Add or update the relevant architectural section.
3. Record significant decisions and trade-offs.
4. Keep implementation details in the appropriate technical documentation.
5. Keep this document focused on **architectural intent, capabilities, security posture and evolution**.

The codebase remains the implementation source of truth.

The documentation explains **why the architecture exists, what it is intended to demonstrate, and where it is going**.
