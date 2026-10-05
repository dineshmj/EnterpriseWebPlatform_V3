# Enterprise Web Platform V3
## Integration Event Catalogue

**Status:** Living document — the single source of truth for Kafka topics and integration-event contracts.

---

## 1. Purpose

This catalogue lists every integration event that crosses a bounded-context boundary: its topic, key, producer, consumers, contract shape and status.

Requirements documents and the Saga plan refer to events **by name** and link here. They do not redefine topics or fields.

Status values (Present, In Progress, Planned, Target) are defined in the [Blueprint §3 Status Legend](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#3-status-legend).

---

## 2. Conventions

| Concern | Convention |
|---|---|
| Topic name | `<context>.<aggregate>.<past-tense-fact>`, lowercase, dot-separated (e.g. `kyc.case.approved`) |
| Event type | PascalCase past tense (e.g. `KycCaseApproved`) |
| Kafka key | The aggregate ID. This guarantees ordering per aggregate within a partition. |
| Publication order | Each Outbox has a database-assigned `sequence` (insertion order). Events raised together are inserted cause-first, and the relay publishes an aggregate's events strictly by `sequence` (never by `occurred_at`, which can tie). |
| Message identity | The Outbox row ID equals the event's `MessageId`, so a row, its Kafka message and any `CausationId` pointing at it share one identifier. |
| Cross-context references | Another context's resource is referenced by a **never-repeating GUID** (e.g. `ApplicationRef`, UUID v7) or a business number, never by its database ID: database IDs restart when a database is recreated, which would make old events match new records. Database IDs may appear in a producer's own events as information only. |
| Enumerations | Serialized as stable upper-case codes (e.g. `IDENTITY_VERIFICATION`), never as numeric ordinals. |
| Delivery | At-least-once. Every consumer must be idempotent on `MessageId`. |
| Producer path | Business transaction → Outbox row (same DB transaction) → relay → Kafka. Producers never publish directly from a request. |
| Payload content | Enough for the consumer's job without calling back. **No document binaries and no unnecessary PII**: send references (IDs, numbers), not data the consumer does not need. |
| Topic creation | Explicit. Kafka auto-creation is disabled; `kafka/Setup-KafkaSecurity.ps1 -Phase Prepare` creates every topic. |
| Access | Each producer and consumer authenticates as its own Kafka user (SCRAM-SHA-512) and may only write or read the topics (and consumer group) listed for it here; see [kafka/README.md](../kafka/README.md). |
| Evolution | Additive changes only within a version. A breaking change needs a new event version, and consumers must tolerate unknown fields. |

---

## 3. The Standard Envelope

Target shape for every integration event:

```text
MessageId          unique per message; the idempotency key for consumers
EventType          e.g. CustomerCreated
SchemaVersion      contract version of the payload (1); changes only for a breaking change
Source             producing bounded context, e.g. customer-onboarding
OccurredAt         business time of the fact
WorkflowId         the long-running business process (saga) this belongs to
CorrelationId      the business interaction that groups related messages
CausationId        MessageId of the message (or request) that caused this one
TraceId            W3C trace context, carried in the Kafka "traceparent" header (not in the body)
InitiatedByUserId  the human who started the workflow (accountability only — never an authorization grant)
Payload            the event-specific body
```

Meaning of the identifiers:

| Identifier | Answers |
|---|---|
| MessageId | Which exact message is this? |
| WorkflowId | Which business process does it belong to? |
| CorrelationId | Which business interaction does it belong to? |
| CausationId | What directly caused it? |
| TraceId | Which technical execution trace? |
| InitiatedByUserId | Which human is accountable for the workflow? |

### 3.1 Current conformance

| Producer | Conformance |
|---|---|
| Customer Onboarding | Full envelope, `Source` = `customer-onboarding`, `SchemaVersion` 1; trace context in Kafka headers. |
| Customer KYC | Full envelope, `Source` = `customer-kyc`, `SchemaVersion` 1; trace context in Kafka headers. (Before increment 1b KYC published a flat shape; its consumer still accepts both, so old messages remain readable.) |
| Compliance | Full envelope, `Source` = `compliance`, `SchemaVersion` 1; trace context in Kafka headers. |

### 3.2 Kafka headers

Both relays add these headers to every message, so infrastructure (tracing, routing, inspection tools) can work without parsing the body:

| Header | Value |
|---|---|
| `traceparent` | W3C trace context of the publish span, which continues the trace of the request that raised the event (stored on the Outbox row as `trace_parent`) |
| `message-id` | The envelope's `MessageId` |
| `event-type` | The envelope's `EventType` |
| `workflow-id`, `correlation-id`, `causation-id` | The envelope's workflow identifiers, when present |

The body remains the source of truth for consumers; headers are a copy for infrastructure.

---

## 4. Topic Catalogue

### 4.1 Customer Onboarding (producer: Customer Onboarding API → `CustomerOutboxPublisher`)

| Event type | Topic | Key | Payload | Consumers | Status |
|---|---|---|---|---|---|
| `CustomerCreated` | `customer.created` | Customer ID | `CustomerId`, `CustomerNumber`, `SubjectId`, `CustomerType`, `Status` | — | Published; no consumer |
| `OnboardingApplicationSubmitted` | `onboarding.application.submitted` | Application ID | `ApplicationRef`, `ApplicationNumber`, `CustomerNumber`, `BranchCode` (branch the application was opened in), `EvidenceDocuments` (`DocumentId`, `DocumentType`) and `Applicant` (`FirstName`, `LastName`, `ResidentialAddress`: the applicant as submitted; no contact details) — both added additively, schema version 1 — plus `ApplicationId` / `CustomerId` (information only) | `KycCaseOpeningSubscriber` → Customer KYC opens one case per `ApplicationRef`; `DocumentInvalidationSubscriber` → Documents Management **attaches** the evidence (ATTACHED: retained, no longer deletable) | Present |
| `OnboardingApplicationStatusChanged` | `onboarding.application.status.changed` | Application ID | `ApplicationRef`, `PreviousStatus`, `NewStatus`, plus `ApplicationId` / `CustomerId` | Notifications (planned) | Published; no consumer yet |
| `OnboardingApplicationRejected` | `onboarding.application.rejected` | Application ID | `ApplicationRef`, `ApplicationNumber`, `CustomerNumber`, `BranchCode`, `RejectedBy` (`KYC` / `COMPLIANCE` / `ACCOUNTS`), `PreviousStatus`, `EvidenceDocuments` (`DocumentId`, `DocumentType`: the evidence recorded at submission) | `DocumentInvalidationSubscriber` → Documents Management invalidates exactly those documents (saga compensation; retained, not deleted) | Present |

### 4.2 Customer KYC (producer: Customer KYC API, in-process Outbox relay)

| Event type | Topic | Key | Consumers | Status |
|---|---|---|---|---|
| `KycCaseCreated` | `kyc.case.created` | KYC case ID | `OnboardingOutcomeSubscriber` (application → KYC_IN_PROGRESS); Notifications (planned) | Present |
| `KycIdentityVerificationApproved` | `kyc.identity.verification.approved` | KYC case ID | — | Published; no consumer |
| `KycIdentityVerificationRejected` | `kyc.identity.verification.rejected` | KYC case ID | — | Published; no consumer |
| `KycDocumentVerificationApproved` | `kyc.document.verification.approved` | KYC case ID | — | Published; no consumer |
| `KycDocumentVerificationRejected` | `kyc.document.verification.rejected` | KYC case ID | — | Published; no consumer |
| `KycCaseApproved` | `kyc.case.approved` | KYC case ID | `OnboardingOutcomeSubscriber` (application → KYC_COMPLETED); `ComplianceCaseOpeningSubscriber` → Compliance opens one case per `ApplicationRef` | Present |
| `KycCaseRejected` | `kyc.case.rejected` | KYC case ID | `OnboardingOutcomeSubscriber` (application → REJECTED) | Present |

Every KYC payload (under `Payload`) carries `KycCaseId`, `ApplicationRef`, `ApplicationNumber` and `CustomerNumber` (`KycCaseCreated` also carries `BranchCode`); Customer Onboarding routes the outcomes by `ApplicationRef`. They also carry the status fields (`Status`, or `PreviousStatus`/`NewStatus`, or the `Stage` with `PreviousStageStatus`/`NewStageStatus`) and the decision fields (`DecisionByUserId`, `DecisionAt`, remarks).

`KycCaseApproved` / `KycCaseRejected` also carry `Applicant` (`FirstName`, `LastName`, `ResidentialAddress`: the applicant as KYC verified them; Compliance screens on it; added additively) and (added in increment 2a, additively) `BranchCode`, `IdentityVerificationByUserId` and `DocumentVerificationByUserId` — the officers who decided each KYC stage. Compliance needs them to keep its case in the same branch and to enforce separation of duties across contexts (a KYC decider may not also clear Compliance for the same customer). Earlier `kyc.case.approved` messages lack them and are dead-lettered by `ComplianceCaseOpeningSubscriber`.

**Cross-topic ordering.** Kafka orders messages only within one partition of one topic. `kyc.case.created` and `kyc.case.approved` are different topics, so a consumer may see the approval first. Consumers must tolerate this; Customer Onboarding's aggregate applies the outstanding transitions and ignores facts it is already beyond.

### 4.3 Compliance (producer: Compliance API, in-process Outbox relay)

| Event type | Topic | Key | Consumers | Status |
|---|---|---|---|---|
| `ComplianceCaseCreated` | `compliance.case.created` | Compliance case ID | `OnboardingOutcomeSubscriber` (application → COMPLIANCE_IN_PROGRESS) | Present |
| `ComplianceCaseApproved` | `compliance.case.approved` | Compliance case ID | `OnboardingOutcomeSubscriber` (application → COMPLIANCE_COMPLETED); `AccountApplicationOpeningSubscriber` → Accounts opens one account application per `ApplicationRef` (uses `DecisionByUserId`, the Compliance approver, for separation of duties) | Present |
| `ComplianceCaseRejected` | `compliance.case.rejected` | Compliance case ID | `OnboardingOutcomeSubscriber` (application → REJECTED) | Present |

Every Compliance payload carries `ComplianceCaseId`, `ApplicationRef`, `ApplicationNumber`, `CustomerNumber` and `BranchCode`. The decision events also carry `Applicant` (`FirstName`, `LastName` only, added additively): Accounts opens the account in that name; the address is not passed on. `ComplianceCaseCreated` adds `KycCaseId` and `Status`; the decision events add `PreviousStatus` / `NewStatus`, `ScreeningOutcome`, `RiskRating`, `DecisionByUserId`, `DecisionAt` and `DecisionRemarks`. Screening progress (provider attempts, retries, assignment, holds) stays inside Compliance and is not published.

### 4.4 Accounts (producer: Accounts API, in-process Outbox relay)

| Event type | Topic | Key | Consumers | Status |
|---|---|---|---|---|
| `AccountApplicationCreated` | `accounts.application.created` | Account application ID | `OnboardingOutcomeSubscriber` (application → ACCOUNT_OPENING_IN_PROGRESS) | Present |
| `AccountOpened` | `accounts.account.opened` | Account application ID | `OnboardingOutcomeSubscriber` (application → **COMPLETED**: the onboarding saga ends) | Present |
| `AccountApplicationRejected` | `accounts.application.rejected` | Account application ID | `OnboardingOutcomeSubscriber` (application → REJECTED, then `OnboardingApplicationRejected` → document invalidation) | Present |
| `AccountOpeningFailed` | `accounts.account.opening.failed` | Account application ID | Compensation of the onboarding (increment 3c) | Published; consumer planned (3c) |

Every Accounts payload carries `AccountApplicationId`, `ApplicationRef`, `ApplicationNumber`, `CustomerNumber` and `BranchCode`. `AccountApplicationCreated` adds `ComplianceCaseId` and `Status`; `AccountOpened` adds `AccountNumber`, `Bsb`, `Product`, `ApprovedByUserId` and `OpenedAt`; `AccountApplicationRejected` adds `PreviousStatus` / `NewStatus`, `DecisionByUserId`, `DecisionAt` and `DecisionRemarks`; `AccountOpeningFailed` adds `Reason`, `Attempts` and `FailedAt`. `AccountOpened`'s `CausationId` is the account officer's approval command, even though a background worker opened the account later. Assignment, holds and the approval itself stay internal.

### 4.5 Dead-letter topics

| Topic | Owner | Contents |
|---|---|---|
| `customer-kyc.case-opening-subscriber.dlq` | `KycCaseOpeningSubscriber` | `onboarding.application.submitted` messages that can never open a case (malformed, wrong type, required fields missing, rejected with 4xx), with the same `dlq-*` headers. Transient failures are never dead-lettered. |
| `compliance.case-opening-subscriber.dlq` | `ComplianceCaseOpeningSubscriber` | `kyc.case.approved` messages that can never open a Compliance case (malformed, wrong type, required fields such as `BranchCode` missing, rejected with 4xx), with the same `dlq-*` headers. Transient failures are never dead-lettered. |
| `documents-management.invalidation-subscriber.dlq` | `DocumentInvalidationSubscriber` | `onboarding.application.submitted` / `onboarding.application.rejected` messages that can never be applied (malformed, wrong type, required fields missing, rejected with 4xx), with the same `dlq-*` headers. Transient failures are never dead-lettered. |
| `accounts.application-opening-subscriber.dlq` | `AccountApplicationOpeningSubscriber` | `compliance.case.approved` messages that can never open an account application (malformed, wrong type, required fields missing, rejected with 4xx), with the same `dlq-*` headers. Transient failures are never dead-lettered. |
| `customer-onboarding.outcome-subscriber.dlq` | `OnboardingOutcomeSubscriber` | Messages that can never be processed (malformed, unknown type, no application, rejected with 4xx), copied unchanged with headers `dlq-reason`, `dlq-original-topic`, `dlq-original-partition`, `dlq-original-offset`, `dlq-consumer-group`, `dlq-failed-at`. Transient failures are never dead-lettered. |

### 4.6 Planned events

| Event type | Topic | Producer | Consumers | Status |
|---|---|---|---|---|
| Payment saga commands and events | `payments.*` | Payments orchestrator and participants | Payments, Accounts | Planned — defined in [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md) |

---

## 5. Known Deviations from the Target

1. **Every consumer has an Inbox and a dead-letter topic.** The KYC API additionally keeps one case per `ApplicationRef`, so even a re-published submission (new `MessageId`) returns the existing case.
2. **The KYC BFF (NestJS) is not instrumented with OpenTelemetry**, so a KYC officer's decision starts a new trace at the KYC API; the workflow is still linked through `WorkflowId` / `CausationId`.