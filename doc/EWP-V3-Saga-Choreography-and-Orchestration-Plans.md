# Enterprise Web Platform V3 — Saga Plans

**Status:** Living document. Customer Onboarding choreography is implemented from submission through the KYC decision and back (both directions between CO and KYC); Compliance and Accounts are planned; Payments orchestration is planned.

---

## Purpose

This document owns the **cross-context workflow design** of EWP V3: which saga style each workflow uses, the order of steps, how failures and compensation behave, and the saga design rules.

| Owned elsewhere | Where |
|---|---|
| Topics and event contracts | [Integration-Event-Catalogue.md](Integration-Event-Catalogue.md) |
| Each context's own states and its reaction to an event | That context's requirements document |
| Human vs service identity, and SoD | [Authorization-Model.md §8–9](Authorization-Model.md#8-separation-of-duties-sod) |

EWP V3 demonstrates both saga styles deliberately:

1. **Choreography** for Customer Onboarding.
2. **Orchestration** for Payments.

---

# 1. Choreography — Customer Onboarding

## 1.1 Intent

There is no central coordinator. Each participating context:

1. performs its own local transaction;
2. writes its Outbox row in the same transaction;
3. has the event published to Kafka;
4. reacts to other contexts' events where its own business requires it;
5. changes and compensates **only the state it owns**.

## 1.2 The event chain

| # | Step | Trigger | Producer → event | Status |
|---|---|---|---|---|
| 1 | Customer and application created; application submitted | Human (agent) via the CO MFE / BFF | CO → `CustomerCreated`, `OnboardingApplicationSubmitted`, `OnboardingApplicationStatusChanged` | Present |
| 2 | KYC case opened, one per application | `onboarding.application.submitted` | KYC → `KycCaseCreated` | Present |
| 3 | Application moves to KYC_IN_PROGRESS | `kyc.case.created` | CO → `OnboardingApplicationStatusChanged` | Present |
| 4 | Human KYC review of two stages (may take days) | KYC officers | KYC → stage events, then `KycCaseApproved` / `KycCaseRejected` | Present |
| 5 | CO records the KYC outcome (KYC_COMPLETED or REJECTED) | `kyc.case.approved` / `rejected` | CO → status changed | Present |
| 6 | Compliance case, screening, human decision | `kyc.case.approved` | Compliance → `ComplianceCaseApproved` / `Rejected` | Planned |
| 7 | Account application, human approval, account opened | `compliance.case.approved` | Accounts → `AccountOpened` / `AccountOpeningFailed` | Planned |
| 8 | Onboarding completes or compensates | Accounts outcome | CO → status changed | Planned |
| 9 | Initiator and other entitled users notified | status-change events | Notifications → SignalR | Planned |

**Why KYC is triggered by submission.** A customer may have more than one application over time. KYC belongs to an *application*, and only submission means the evidence is complete.

**Steps 3 and 5 run in Customer Onboarding's own worker** (`CustomerOnboardingKycSubscriber`). It delivers each KYC fact to the CO API, where the `OnboardingApplication` aggregate decides the transition. Because the facts arrive on different topics, they can arrive out of order or more than once: the aggregate applies only the transitions still outstanding, and the Inbox makes each fact count once.

## 1.3 The MFE / BFF boundary

Document upload stays at the MFE / BFF boundary. The CO BFF uploads evidence to Documents Management with its M2M identity, so the CO API never receives document content. This is lightweight synchronous orchestration of **one user request**, not saga coordination. The BFF never owns long-running state, distributed compensation, Kafka choreography or business-recovery decisions.

If that request fails partway, the BFF removes documents it uploaded in the same request, and any customer or application already created remains in DRAFT for retry. This is **request-level cleanup of something never submitted**. It is not saga compensation, so it does not conflict with the retention rule in §1.5.

## 1.4 Long-running human approval

Human review is a **persisted business state**, not a waiting process. No request, thread or process stays alive while an officer is away. The officer's decision is a new local transaction whose Outbox event resumes the workflow.

## 1.5 Rejection and compensation

A business rejection (KYC, compliance or account) is published as an event. Each interested context compensates its own state:

- CO marks the application REJECTED (or COMPENSATING → … for later failures).
- KYC or Compliance closes its case.
- Documents Management marks documents INVALIDATED — **retained, not deleted**, because regulation and audit may require retention.

No context rolls back another context's database.

## 1.6 Technical vs business failure

| | Technical failure | Business failure |
|---|---|---|
| Examples | API unavailable, timeout, Kafka or network error | KYC rejects evidence; compliance rejects; account cannot be opened |
| Handling | Timeout → bounded retry with backoff and jitter → circuit breaker → recovery state or dead-letter | Domain event → compensation by each owner → final business outcome |

> **Retry handles transient technical failure. Compensation handles business failure, or the inability to finish after work has already been done.**

---

# 2. Orchestration — Payments

## 2.1 Intent

A **Payment Saga Orchestrator**, owned by the Payments context, persists the workflow state and decides the next step. Participants (Payments, Accounts) own their data and perform their own state changes. The orchestrator never touches their databases. Commands and replies may still travel over Kafka:

> **Kafka does not imply choreography.** The difference is where the workflow decision lives.

## 2.2 Persisted saga state

```text
SagaId, PaymentId, CurrentStep, Status, WorkflowId, CorrelationId, CausationId, InitiatedByUserId, CreatedAt, UpdatedAt
```

## 2.3 Successful path

```text
Payment initiated ─► Validate ─► Reserve funds (Accounts) ─► Human approval (if the tier requires it) ─► Execute ─► Completed
```

When approval is required, the orchestrator persists `PAYMENT_APPROVAL_PENDING` and stops. The officer's decision event resumes it, possibly days later.

## 2.4 Compensation

```text
Execute fails ─► Compensation required ─► Release reserved funds (Accounts) ─► Funds released ─► Payment FAILED
```

## 2.5 Compensation failure

```text
Release funds ─✗─► retry #1 ─✗─► retry #2 ─✗─► retry #3 ─✗─► circuit breaker OPEN
              ─► COMPENSATION_REQUIRED / COMPENSATION_FAILED ─► operations recovery
```

The saga must never claim a rollback that did not happen. It records the truth and exposes a recoverable operational state.

Payment business states, approval tiers and rules: [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md).

---

# 3. Resilience Is Not the Saga

Retry, timeout and circuit breaker protect **individual technical interactions**. They are not compensation, and they are not the saga:

- a transient failure that is still unresolved after bounded retries leads to a recovery state;
- a permanent business failure leads to a saga transition and compensation.

---

# 4. Choreography vs Orchestration in EWP V3

| Concern | Customer Onboarding | Payments |
|---|---|---|
| Style | Choreography | Orchestration |
| Central coordinator | No | Yes (owned by Payments) |
| Workflow state | Distributed: each context's own state | Explicit persisted saga state, plus each context's own state |
| Kafka, Outbox, Inbox | Yes | Yes |
| Human approval | KYC, compliance and account review | Payment approval by tier |
| Retry, timeout, circuit breaker | Yes | Yes |
| Compensation | Each owner reacts to rejection events | The orchestrator issues compensating commands |
| Cross-service database access | Never | Never |

> **Event-driven messaging and saga choreography are not synonyms.**

---

# 5. Implementation Checklist

**Customer Onboarding — choreography**

- [x] CO publishes business events through its Outbox, with workflow, correlation, causation and initiator data
- [x] KYC subscriber consumes, authenticates with M2M and creates the KYC case
- [x] Human KYC approval / rejection as domain state plus Outbox events
- [x] KYC triggered by `onboarding.application.submitted` (one case per application)
- [x] CO consumes `kyc.*` outcomes (`CustomerOnboardingKycSubscriber`)
- [x] Inbox / idempotency in the CO consumer
- [x] Timeout, retry, circuit breaker and dead-letter handling in the CO consumer
- [ ] The same Inbox and dead-letter handling in `CustomerKycSubscriber`
- [ ] Compliance participant
- [ ] Accounts participant
- [ ] Compensation paths, including DM document invalidation
- [ ] Notifications to the initiator and other entitled users

**Payments — orchestration** (after the choreography is stable)

1. Payment state machine and saga state
2. Commands and events, added to the Event Catalogue
3. Orchestrator with Outbox and Inbox
4. Retry, timeout and circuit breaker
5. Successful scenario
6. Business failure with compensation
7. Deliberate compensation failure and recovery
8. Human approval as a long-running state
9. Notifications
10. End-to-end observability by WorkflowId, CorrelationId and CausationId

---

# 6. Saga Design Rules

These apply in addition to the platform principles in the [Blueprint §2](Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md#2-architectural-principles):

1. A context compensates only the state it owns.
2. BFFs never own long-running saga state and never coordinate distributed transactions.
3. The Shell and the Application Workspace never acquire workflow logic.
4. Human approval is a persisted state, not a running process.
5. Retry is not compensation; a circuit breaker is not compensation.
6. Never report a successful rollback when compensation failed.
7. Every saga step preserves causality and the initiating human. The initiator is accountability, not authorization.
8. Business-relevant documents are invalidated and retained, not deleted, as compensation.
