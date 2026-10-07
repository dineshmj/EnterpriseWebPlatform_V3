# Enterprise Web Platform V3 — Saga Plans

**Status:** Living document. Customer Onboarding choreography is implemented end to end, from submission through the KYC, Compliance and Accounts decisions to a COMPLETED onboarding, including compensation of a failed account opening; Payments orchestration is implemented for the backend (step 5a: reserve, send, settle, compensation, timeouts, compensation failure); its screens, human approval and notifications follow in step 5b.

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
| 6 | Compliance case opened, external screening (asynchronous, retried), human decision | `kyc.case.approved` | Compliance → `ComplianceCaseCreated`, then `ComplianceCaseApproved` / `Rejected`; CO → COMPLIANCE_IN_PROGRESS / COMPLETED / REJECTED | Present |
| 7 | Account application, human approval, account opened by the core-banking system (asynchronous, retried, idempotent) | `compliance.case.approved` | Accounts → `AccountApplicationCreated`, then `AccountOpened` / `AccountApplicationRejected` / `AccountOpeningFailed`; CO → ACCOUNT_OPENING_IN_PROGRESS | Present |
| 8 | Onboarding completes (or compensates after a failed opening) | Accounts outcome | CO → COMPLETED / COMPENSATING → REJECTED (`RejectedBy: ACCOUNT_OPENING`) | Present |
| 9 | Initiator and the next team notified | the KYC, Compliance and Accounts events | NotificationsSubscriber → Notifications API → SignalR → Shell bell and toasts | Present |

**Why KYC is triggered by submission.** A customer may have more than one application over time. KYC belongs to an *application*, and only submission means the evidence is complete.

**Steps 3 and 5 run in Customer Onboarding's own worker** (`OnboardingOutcomeSubscriber`). It delivers each KYC fact to the CO API, where the `OnboardingApplication` aggregate decides the transition. Because the facts arrive on different topics, they can arrive out of order or more than once: the aggregate applies only the transitions still outstanding, and the Inbox makes each fact count once.

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

**Present (onboarding rejection):** CO records the evidence document IDs at submission. On a KYC or Compliance rejection it publishes `OnboardingApplicationRejected` (topic `onboarding.application.rejected`) naming them, and DM's `DocumentInvalidationSubscriber` invalidates exactly those documents of the application's branch (idempotent: Inbox, and invalidating twice is a no-op). The cases are already final in KYC and Compliance when they reject, so no further case compensation is needed there.

## 1.6 Technical vs business failure

| | Technical failure | Business failure |
|---|---|---|
| Examples | API unavailable, timeout, Kafka or network error | KYC rejects evidence; compliance rejects; account cannot be opened |
| Handling | Timeout → bounded retry with backoff and jitter → circuit breaker → recovery state or dead-letter | Domain event → compensation by each owner → final business outcome |

> **Retry handles transient technical failure. Compensation handles business failure, or the inability to finish after work has already been done.**

---

# 2. Orchestration — Payments

## 2.1 Intent

A **Payment Saga Orchestrator**, owned by the Payments context, persists the workflow state and decides every next step. The participant (Accounts) and the external payment network only do what they are asked; the orchestrator never touches their databases. Commands and replies still travel over Kafka:

> **Kafka does not imply choreography.** The difference is where the workflow decision lives.

The difference in one sentence: in choreography everyone reacts to news; in orchestration one owner gives instructions and waits for answers.

| | Choreography (onboarding) | Orchestration (payments) |
|---|---|---|
| Who knows the sequence? | Nobody as a whole: each context knows its own cue ("when KYC approves, I open a case") | Only the orchestrator (`PaymentSaga`) |
| Messages | **Events**, facts in the past tense: `KycCaseApproved` | **Commands** (`ReserveFunds`) and **replies** (`FundsReserved`) |
| Where the workflow state lives | Pieced together from each context (CO mirrors it by listening) | One row in `payment_sagas` |
| Compensation | Each context reacts to a rejection event on its own | The orchestrator sends an explicit undo command: `ReleaseFunds` |
| Adding a step | A new subscriber somewhere | A change to the orchestrator |
| "Where is payment 42?" | Ask several contexts | Read one row (and its timeline) |

The infrastructure is the same in both: Outbox, Inbox, Kafka, and courier workers that read Kafka and call an API with an M2M token. The couriers decide nothing in either style.

## 2.2 Who does what (implemented, step 5a)

![Payment saga orchestration](Payment%20Saga%20Orchestration%20(corrected).png)

| Piece | Runs in | Role |
|---|---|---|
| `PaymentSaga` | Payments API | The orchestrator: a persisted state machine, the ONLY place decisions are made. |
| `Payment` | Payments API | The business state people see; changed only by its saga. |
| `SagaStepRunner` | Payments API (background service) | The saga's timer: calls the payment network (direct HTTP, Idempotency-Key, timeout, retry, circuit breaker) and wakes sagas whose reply is overdue. Decides nothing. |
| Outbox publisher | Payments API (background service) | Commands to `accounts.commands`, outcomes to `payments.payment.events`. |
| `AccountsCommandSubscriber` | console worker (Accounts) | Courier: commands → Accounts API. |
| Accounts API | participant | Reserves, settles or releases in ONE local transaction (`funds_holds` + the account's balance), replies through its own Outbox on `accounts.funds.replies`. Knows nothing about the saga. |
| `PaymentsSagaReplySubscriber` | console worker (Payments) | Courier: replies → the Payments API's internal endpoint → the saga. |
| Payment Network Simulator | external | Accepts, refuses (422) or fails like a real third party. |

`POST /v1/payments` saves the payment, the saga and the first command in one transaction and answers **202 Accepted** at once. Each later step is a short, separate piece of work (a reply arrived, a timer is due) that locks the saga row, decides, and saves the new state with the next command in one transaction. Nothing waits in memory; a restart resumes from the saved step.

## 2.3 Persisted saga state

```text
payment_sagas:        Id (SagaId), PaymentId, PaymentRef, Step, Status, Attempts, NextCheckAt, LastError,
                      CurrentCommandId, LastMessageId (the next message's CausationId), WorkflowId, CorrelationId,
                      InitiatedByUserId, trace_parent, CreatedAt, UpdatedAt, Version
payment_saga_history: every command sent, reply received / ignored, timeout, network call and decision (the timeline)
```

## 2.4 Successful path

```text
RESERVE_FUNDS ─FundsReserved─► (AWAIT_APPROVAL) ─► SEND_TO_NETWORK ─accepted─► SETTLE_FUNDS ─FundsSettled─► DONE (COMPLETED)
```

Above the approval tier (`Payments:ApprovalThreshold`) the saga stops at `AWAIT_APPROVAL` (payment `PENDING_APPROVAL`) with no timer and publishes `PaymentApprovalRequired` (the branch's payments officers are notified). The officer's decision resumes it, possibly days later: approve → send to the network; reject → release the funds → REJECTED. The approver is never the initiator and approves only within their clearance's limit. The reservation comes BEFORE the approval, so the money is still there when the officer approves.

## 2.5 Compensation

```text
RESERVE_FUNDS ─FundsReservationFailed─► DONE (REJECTED: nothing reserved, nothing to undo)
SEND_TO_NETWORK ─refused / unavailable after N tries─► RELEASE_FUNDS ─FundsReleased─► DONE (FAILED: funds released)
```

## 2.6 Timeouts and compensation failure

```text
no reply in time ─► resend the same command (Accounts is idempotent per PaymentRef); timeout doubles 30 s → 5 min
RESERVE not confirmed after N tries ─► release to be sure ─► FAILED
SETTLE not confirmed               ─► keep trying (the money already left through the network)
RELEASE not confirmed after N tries ─► STUCK, payment COMPENSATION_FAILED ─► operations "Retry release" (step 5c)
                                       a late FundsReleased still resolves it
```

The saga never claims a rollback that did not happen. It records the truth, publishes `PaymentCompensationFailed`, turns `/health/ready` Degraded, and exposes a recoverable operational state.

Payment business states, approval tiers and rules: [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md). How to run it: [Payments API README](../src/Microservices/Payments/API/README.md).

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
- [x] KYC Case Opening Subscriber consumes, authenticates with M2M and creates the KYC case
- [x] Human KYC approval / rejection as domain state plus Outbox events
- [x] KYC triggered by `onboarding.application.submitted` (one case per application)
- [x] CO consumes `kyc.*` outcomes (`OnboardingOutcomeSubscriber`)
- [x] Inbox / idempotency in the CO consumer
- [x] Timeout, retry, circuit breaker and dead-letter handling in the CO consumer
- [x] The same Inbox and dead-letter handling in `KycCaseOpeningSubscriber` (both workers share one consume loop: `AsyncWorkflows.Infrastructure.Subscribers`)
- [x] Compliance participant (`ComplianceCaseOpeningSubscriber`; outcomes via `OnboardingOutcomeSubscriber`)
- [x] Accounts participant (`AccountApplicationOpeningSubscriber`; outcomes via `OnboardingOutcomeSubscriber`)
- [x] Compensation on rejection: DM document invalidation (`DocumentInvalidationSubscriber`)
- [x] Compensation of later failures (account opening; COMPENSATING)
- [x] Notifications to the initiator and the next team (stored, pushed and shown in the Shell)

**Payments — orchestration**

- [x] Payment state machine and saga state (5a)
- [x] Commands, replies and events, added to the Event Catalogue (5a)
- [x] Orchestrator with Outbox and Inbox (5a)
- [x] Retry, timeout and circuit breaker (5a)
- [x] Successful scenario (5a)
- [x] Business failure with compensation (5a)
- [ ] Deliberate compensation failure and recovery: the failure state is present (5a); the operations "Retry release" action follows (5c)
- [x] Human approval as a long-running state: the saga waits with no timer; the payments officer's approve / reject resumes it (5b-2)
- [x] Notifications: approval required → the branch's payments officers; outcomes → the initiator (5b-2)
- [x] End-to-end observability by WorkflowId, CorrelationId, CausationId and one trace per payment (5a)

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