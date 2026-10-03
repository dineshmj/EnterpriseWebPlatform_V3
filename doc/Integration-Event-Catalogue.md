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
| Enumerations | Serialized as stable upper-case codes (e.g. `IDENTITY_VERIFICATION`), never as numeric ordinals. |
| Delivery | At-least-once. Every consumer must be idempotent on `MessageId`. |
| Producer path | Business transaction → Outbox row (same DB transaction) → relay → Kafka. Producers never publish directly from a request. |
| Payload content | Enough for the consumer's job without calling back. **No document binaries and no unnecessary PII**: send references (IDs, numbers), not data the consumer does not need. |
| Topic creation | Explicit. Kafka auto-creation is disabled; see ReadMe.txt §3 for the creation commands. |
| Evolution | Additive changes only within a version. A breaking change needs a new event version, and consumers must tolerate unknown fields. |

---

## 3. The Standard Envelope

Target shape for every integration event:

```text
MessageId          unique per message; the idempotency key for consumers
EventType          e.g. CustomerCreated
SchemaVersion      (target) contract version
Source             producing bounded context, e.g. customer-onboarding
OccurredAt         business time of the fact
WorkflowId         the long-running business process (saga) this belongs to
CorrelationId      the business interaction that groups related messages
CausationId        MessageId of the message (or request) that caused this one
TraceId            (target) W3C trace context, carried in Kafka headers
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
| Customer Onboarding | Uses the envelope: `MessageId`, `EventType`, `Source`, `OccurredAt`, `WorkflowId`, `CorrelationId`, `CausationId`, `InitiatedByUserId`, `Payload`. Missing: `SchemaVersion`, `TraceId`. |
| Customer KYC | **Flat** message: the envelope fields sit beside the payload fields, and there is no `Source` and no `Payload` wrapper. Target: adopt the standard envelope. |

Workflow identifiers currently travel only in the message body, not in Kafka headers.

---

## 4. Topic Catalogue

### 4.1 Customer Onboarding (producer: Customer Onboarding API → `CustomerOutboxPublisher`)

| Event type | Topic | Key | Payload | Consumers | Status |
|---|---|---|---|---|---|
| `CustomerCreated` | `customer.created` | Customer ID | `CustomerId`, `CustomerNumber`, `SubjectId`, `CustomerType`, `Status` | `CustomerKycSubscriber` (interim KYC trigger — see §5) | Present |
| `OnboardingApplicationSubmitted` | `onboarding.application.submitted` | Application ID | `ApplicationId`, `CustomerId`, `ApplicationNumber` | Customer KYC (target trigger) | Published; **no consumer yet** |
| `OnboardingApplicationStatusChanged` | `onboarding.application.status.changed` | Application ID | `ApplicationId`, `CustomerId`, `PreviousStatus`, `NewStatus` | Notifications (planned) | Published; **no consumer yet** |

### 4.2 Customer KYC (producer: Customer KYC API, in-process Outbox relay)

| Event type | Topic | Key | Consumers | Status |
|---|---|---|---|---|
| `KycCaseCreated` | `kyc.case.created` | KYC case ID | Notifications (planned) | Published; no consumer |
| `KycIdentityVerificationApproved` | `kyc.identity.verification.approved` | KYC case ID | — | Published; no consumer |
| `KycIdentityVerificationRejected` | `kyc.identity.verification.rejected` | KYC case ID | — | Published; no consumer |
| `KycDocumentVerificationApproved` | `kyc.document.verification.approved` | KYC case ID | — | Published; no consumer |
| `KycDocumentVerificationRejected` | `kyc.document.verification.rejected` | KYC case ID | — | Published; no consumer |
| `KycCaseApproved` | `kyc.case.approved` | KYC case ID | Customer Onboarding, Compliance (planned) | Published; **no consumer yet** |
| `KycCaseRejected` | `kyc.case.rejected` | KYC case ID | Customer Onboarding (planned) | Published; **no consumer yet** |

The KYC payload fields are `KycCaseId` and `CustomerNumber`, plus the status fields (`Status`, or `PreviousStatus`/`NewStatus`, or the `Stage` with `PreviousStageStatus`/`NewStageStatus`) and the decision fields (`DecisionByUserId`, `DecisionAt`, remarks).

### 4.3 Planned events

| Event type | Topic | Producer | Consumers | Status |
|---|---|---|---|---|
| `ComplianceCaseApproved` / `ComplianceCaseRejected` | `compliance.case.approved` / `.rejected` | Compliance | Customer Onboarding, Accounts | Planned |
| `AccountOpened` / `AccountOpeningFailed` | `accounts.account.opened` / `accounts.account.opening.failed` | Accounts | Customer Onboarding | Planned |
| Payment saga commands and events | `payments.*` | Payments orchestrator and participants | Payments, Accounts | Planned — defined in [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md) |

---

## 5. Known Deviations from the Target

1. **KYC trigger.** KYC currently opens one case per *customer* from `customer.created`. The target is one KYC case per *onboarding application*, triggered by `onboarding.application.submitted` (see the [Saga plan](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md)).
2. **No consumer** exists yet for the `onboarding.application.*` or `kyc.*` topics, so the choreography stops after the KYC decision.
3. **No dead-letter topics.** A consumer that cannot process a message cannot park it.
4. **No Inbox.** Consumer idempotency currently relies on a business unique key (`customer_number` in KYC), not on `MessageId`.
5. **KYC events do not use the standard envelope** (see §3.1).
