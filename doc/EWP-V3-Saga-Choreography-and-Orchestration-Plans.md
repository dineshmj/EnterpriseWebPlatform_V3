# Enterprise Web Platform V3 — Saga Plans

## Purpose

This document records the two Saga patterns planned for demonstration in EnterpriseWebPlatform V3 (EWP V3):

1. **Saga with Choreography** — used for the Customer Onboarding workflow.
2. **Saga with Orchestration** — proposed for the Payments workflow.

The intent is to demonstrate both approaches deliberately, rather than treating either pattern as universally preferable.

---

# 1. Saga with Choreography — Customer Onboarding

## 1.1 Architectural Intent

The existing Customer Onboarding workflow will continue to use **Saga with Choreography**.

This preserves the event-driven architecture already established between Customer Onboarding (CO), Documents Management (DM), KYC, and subsequent microservices.

The workflow is driven by domain/integration events published through Kafka. There is no central Saga orchestrator directing every participant.

Conceptually:

```text
Customer Onboarding
        |
        | Onboarding / Customer event
        v
      Kafka
        |
        v
       KYC
        |
        | KYC event
        v
      Kafka
        |
        v
   Next participant
```

Each participating bounded context:

1. Performs its own local business transaction.
2. Writes its own transactional Outbox entry.
3. Publishes the resulting event to Kafka.
4. Reacts to events from other bounded contexts where appropriate.
5. Changes only the business state that it owns.

---

## 1.2 Document Uploading Remains at the MFE/BFF Boundary

The existing document-upload design should **not** be moved into the Customer Onboarding API merely to make the Saga more centralized.

The Customer Onboarding MFE/BFF can perform the lightweight synchronous orchestration required to upload documents to the Documents Management API using an appropriate M2M/service identity.

This avoids introducing unnecessary application/node hops for document-upload scenarios.

The BFF is not the Saga coordinator.

It should not own:

- distributed Saga state;
- long-running workflow execution;
- distributed compensation;
- Kafka workflow choreography;
- business recovery decisions.

---

## 1.3 Long-Running Human Approval

The choreography-based workflow can span days.

For example:

```text
Day 1
Susan starts Camilla Parker's onboarding
        |
        v
Customer Onboarding
        |
        v
Kafka
        |
        v
KYC Case
        |
        v
WAITING FOR HUMAN REVIEW
```

The system does not keep an HTTP request, thread, or process alive while waiting.

The relevant bounded context persists its business state.

Later:

```text
Day 3
KYC Officer reviews Camilla Parker's KYC documents
        |
        +---- Approve
        |
        +---- Reject
```

A human decision changes KYC business state and produces an event through the KYC Outbox.

The event then becomes the trigger for the next part of the distributed workflow.

---

## 1.4 Rejection and Compensation

If the KYC Officer rejects a document or the KYC process, the KYC bounded context publishes a rejection event.

For example:

```text
KYC_REJECTED
      |
      v
    Kafka
      |
      +--------------------+
      |                    |
      v                    v
     CO              Other interested
                      participants
```

The appropriate participants react to the event and compensate **only the business state they own**.

The system must not perform cross-microservice database rollback.

For example:

- CO can mark the onboarding application as rejected/failed.
- KYC can close/cancel its KYC case.
- DM can apply the appropriate document lifecycle state if the business rules require it.

Document deletion should not automatically be assumed to be the correct compensation. In a banking-style system, retention, auditability, and regulatory requirements may require documents to be retained while being marked invalidated or otherwise unusable.

---

## 1.5 Technical Failure vs Business Failure

The choreography workflow must distinguish between technical and business failures.

### Technical failure

Examples:

- temporary API unavailability;
- network failure;
- transient timeout;
- Kafka delivery/processing failure.

Typical handling:

```text
Transient failure
      |
      v
Retry with bounded backoff
      |
      +---- recovered ----> Continue
      |
      +---- exhausted ----> Technical failure / recovery state
```

Circuit breakers, timeouts, retries, and idempotent consumers protect the technical execution.

### Business failure

Examples:

- KYC Officer rejects submitted identity evidence;
- compliance decision rejects the onboarding;
- a business rule prevents account creation.

Typical handling:

```text
Business rejection
       |
       v
Domain event
       |
       v
Saga compensation
       |
       v
Final business outcome
```

The key distinction is:

> **Retry handles transient technical failure; compensation handles business failure or an inability to complete a distributed business transaction after the relevant work has already occurred.**

---

# 2. Saga with Orchestration — Payments

## 2.1 Architectural Intent

The Payments domain is proposed as the demonstration area for **Saga with Orchestration**.

This provides EWP V3 with a deliberate second Saga style without requiring the existing Customer Onboarding choreography to be rewritten.

The orchestration-based workflow has a central workflow component that maintains the distributed business process state and decides which step should happen next.

Conceptually:

```text
                 Payment Saga
                  Orchestrator
                       |
          +------------+------------+
          |            |            |
          v            v            v
      Payments      Accounts     Other
         API           API       services
```

The orchestrator coordinates the workflow but does not own the business data belonging to the participating bounded contexts.

---

## 2.2 Why Payments Is a Good Candidate

A payment workflow can naturally contain multiple dependent business steps, for example:

```text
Payment Initiated
       |
       v
Validate Payment
       |
       v
Reserve Funds
       |
       v
Human / Risk Approval
       |
       v
Execute Payment
       |
       v
Payment Completed
```

The workflow can therefore demonstrate:

- persisted Saga state;
- multiple participating services;
- asynchronous commands/events;
- human approval;
- retry and timeout;
- circuit breaker;
- compensation;
- compensation failure;
- recovery of a long-running workflow.

---

## 2.3 Long-Running Orchestration

The orchestrator must not hold an HTTP request open while waiting for a human decision.

For example:

```text
Day 1
Payment initiated
       |
       v
Validation completed
       |
       v
Funds reservation completed
       |
       v
AWAITING HUMAN APPROVAL
```

The orchestrator persists its state:

```text
SagaId
PaymentId
CurrentStep
Status
CorrelationId
CausationId
InitiatedByUserId
CreatedAt
UpdatedAt
```

The orchestration execution can stop.

Days later:

```text
Payment Officer
       |
       v
Approve / Reject
       |
       v
Payment domain transaction
       |
       v
Approval event
       |
       v
Saga Orchestrator
```

The orchestrator loads the persisted Saga state and continues from the appropriate state.

---

# 3. Orchestrated Payment — Successful Path

A representative successful workflow is:

```text
Payment Initiated
       |
       v
Validate Payment
       |
       v
Reserve Funds
       |
       v
Human Approval
       |
       v
Execute Payment
       |
       v
Payment Completed
```

The orchestrator explicitly understands the workflow sequence.

It may communicate with participating services through commands and events rather than requiring synchronous service-to-service calls.

Kafka can therefore still be used in the orchestration model.

> **Kafka does not imply choreography.**

The distinction is where the workflow decision-making resides.

---

# 4. Orchestrated Payment — Compensation

Consider:

```text
Payment Initiated          ✓
Validation                 ✓
Funds Reserved             ✓
Human Approval             ✓
Payment Execution          ✗
```

The payment cannot be completed.

The orchestrator determines that the previously completed funds reservation requires compensation.

```text
Payment Execution Failed
        |
        v
Compensation Required
        |
        v
Release Reserved Funds
        |
        v
Funds Released
        |
        v
Payment Failed
```

The orchestrator coordinates the compensation, while the Accounts/Payments bounded context performs the actual state change it owns.

The orchestrator must not directly manipulate another microservice's database.

---

# 5. Compensation Failure

Compensation itself can fail.

For example:

```text
Payment Execution
       |
       X
     Failed
       |
       v
Release Funds
       |
       X
Account service unavailable
       |
       v
Retry #1
       |
       v
Retry #2
       |
       v
Retry #3
       |
       v
Circuit Breaker OPEN
```

The Saga must **not** falsely report that the transaction has been completely rolled back.

Instead, it should enter an explicit recovery state, for example:

```text
COMPENSATION_REQUIRED
```

or:

```text
COMPENSATION_FAILED
```

This state can then be surfaced to operations/support tooling for controlled recovery.

This demonstrates an important enterprise principle:

> A distributed transaction cannot always be made to appear atomically rolled back. The system must preserve the truth about incomplete compensation and provide a recoverable operational state.

---

# 6. Retry, Timeout and Circuit Breaker

These are resilience mechanisms around individual technical interactions.

They are not themselves the Saga.

For example:

```text
Saga Orchestrator
       |
       v
Call / Command
       |
       +---- Timeout
       |
       +---- Retry
       |
       +---- Circuit Breaker
       |
       v
Service
```

The policy should distinguish:

### Transient technical failure

```text
Service unavailable
       |
       v
Bounded retry
       |
       +---- success ----> Continue Saga
       |
       +---- exhausted --> Recovery / technical failure state
```

### Permanent business failure

```text
Business rejection
       |
       v
Saga transition
       |
       v
Compensation
```

---

# 7. Human Approval in the Orchestrated Saga

Human approval is a business state, not a technical wait.

Example:

```text
PAYMENT_APPROVAL_PENDING
```

The orchestrator persists this state and stops active execution.

A human later makes a decision:

```text
Approve
   |
   v
PaymentApproved
   |
   v
Orchestrator resumes
```

or:

```text
Reject
   |
   v
PaymentRejected
   |
   v
Orchestrator
   |
   v
Compensation, where required
```

This allows the orchestrated Saga to span days without keeping application processes alive.

---

# 8. Human Identity and Service Identity

The two Saga styles share the same identity model.

A human initiating a workflow is represented by:

```text
InitiatedByUserId
```

A service/subscriber executing an M2M operation has a separate service identity.

For example:

```text
Susan
  |
  | initiates
  v
Payment Saga
  |
  | M2M execution
  v
Payment Service
```

The M2M identity must not be mistaken for the human originator.

The event/envelope metadata should preserve appropriate traceability, including:

```text
MessageId
EventType
Source
OccurredAt
WorkflowId
CorrelationId
CausationId
InitiatedByUserId
Payload
```

`InitiatedByUserId` is workflow accountability/context; it is not, by itself, an authorization grant.

---

# 9. Choreography vs Orchestration in EWP V3

The two demonstrations intentionally have different responsibilities.

| Concern | Customer Onboarding | Payments |
|---|---|---|
| Saga style | Choreography | Orchestration |
| Central coordinator | No | Yes |
| Kafka | Yes | Yes |
| Transactional Outbox | Yes | Yes |
| Inbox / idempotency | Yes | Yes |
| Human approval | KYC review | Payment approval, where applicable |
| Long-running workflow | Yes | Yes |
| Retry | Yes | Yes |
| Timeout | Yes | Yes |
| Circuit breaker | Yes | Yes |
| Compensation | Yes | Yes |
| Workflow state | Distributed across contexts | Explicit persisted orchestration state |
| Business data ownership | Each service owns its data | Each service still owns its data |
| Cross-service DB access | Never | Never |

The important architectural lesson is:

> **Event-driven messaging and Saga choreography are not synonyms.**

Both the choreography and orchestration demonstrations can use Kafka.

The difference is:

```text
Choreography:
Service A emits an event
        |
        v
Service B decides how to react
        |
        v
Service B emits another event
```

versus:

```text
Orchestration:
Saga Orchestrator
        |
        +---- command/event ---> Service A
        |
        +---- command/event ---> Service B
        |
        +---- command/event ---> Service C
```

---

# 10. Overall EWP V3 Saga Demonstration

The intended architecture is therefore:

```text
                  EWP V3 SAGA DEMONSTRATIONS
                              |
              +---------------+---------------+
              |                               |
              v                               v
       CUSTOMER ONBOARDING                PAYMENTS
              |                               |
              v                               v
       CHOREOGRAPHY                     ORCHESTRATION
              |                               |
        CO / DM / KYC                    Payment Saga
              |                           Orchestrator
              |                               |
        Kafka events                  Commands / Events
              |                               |
        Human KYC review              Payments / Accounts
              |                               |
        Compensation                   Human approval
                                              |
                                         Compensation
```

This gives EWP V3 a deliberate demonstration of both distributed transaction coordination models without forcing the entire platform into one pattern.

---

# 11. Planned Implementation Sequence

## Customer Onboarding — Choreography

Continue the existing implementation first:

1. Customer Onboarding publishes business events through its Outbox.
2. Kafka subscribers consume events.
3. KYC participates in the workflow.
4. Human KYC approval/rejection is represented as domain state and events.
5. Compensation is performed through events by the bounded contexts that own the affected state.
6. Retry, timeout, circuit breaker and idempotency are applied to technical interactions.
7. `InitiatedByUserId`, `CorrelationId`, `CausationId` and related metadata are propagated.
8. SignalR can notify the human workflow initiator about relevant workflow outcomes.

## Payments — Orchestration

Implement separately after the choreography workflow is stable:

1. Define the Payment Saga state machine.
2. Define persisted Saga state.
3. Define participating Payments/Accounts responsibilities.
4. Define commands and events.
5. Implement the Saga Orchestrator.
6. Add transactional Outbox and Inbox/idempotency.
7. Add retry, timeout and circuit breaker policies.
8. Implement a successful payment scenario.
9. Implement a business failure requiring compensation.
10. Implement a deliberate compensation failure.
11. Demonstrate recovery from the compensation-required state.
12. Add human approval as a long-running state where appropriate.
13. Integrate SignalR workflow notifications.
14. Add observability using WorkflowId, CorrelationId and CausationId.

---

# 12. Design Rules

The following rules should remain consistent across both demonstrations:

1. **No cross-microservice database access.**
2. **A service compensates only the business state it owns.**
3. **BFFs do not own long-running Saga state.**
4. **BFFs do not become distributed transaction coordinators.**
5. **Human approval is a persisted business state, not a running process.**
6. **M2M identity is distinct from human identity.**
7. **Retry is not compensation.**
8. **Circuit breaker is not compensation.**
9. **At-least-once delivery requires idempotent consumers.**
10. **Outbox records must be created atomically with the associated business transaction.**
11. **The system must not report successful rollback when compensation has actually failed.**
12. **Workflow/audit metadata should preserve causality and the initiating human identity.**
13. **The Shell remains a presentation/composition boundary and does not become the Saga coordinator.**
14. **Application Workspace remains a passive human-context aid and does not acquire business workflow logic.**

---

## Status

**Planning document — implementation to follow.**

The Customer Onboarding choreography is already partially implemented and should be extended without redesigning the existing document-upload interaction.

The Payments orchestration is a planned second Saga demonstration and should be designed and implemented independently of the Customer Onboarding choreography.
