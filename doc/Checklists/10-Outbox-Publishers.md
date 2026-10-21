# 10 — Outbox publishers

Deployables:
- **Separate worker:** the Customer Onboarding Outbox relay (`src/AsyncWorkflows/Publishers`).
- **In-process relays:** inside the KYC, Compliance, Accounts and Payments APIs (`Infrastructure/Messaging/*OutboxPublisher.cs`).

## 1. Reliability

- [x] **The relay reads only committed rows** that the business transaction wrote together with its state change. *Because* nothing is published that did not happen, and nothing that happened is lost.
- [x] **`FOR UPDATE SKIP LOCKED`:** several instances share the work without taking the same row.
- [x] **Order is kept per aggregate:** the oldest message of an aggregate goes first, and a parked message holds back later ones of the same aggregate.
- [x] **Attempts are bounded with exponential back-off.** After `MaxAttempts` a row is parked, not retried forever. Payments' `MaxAttempts` and delay come from configuration; the others are constants.
- [x] **The producer uses `Acks = All` and `EnableIdempotence = true`.** *Because* the broker must not lose or duplicate a message on its side.
- [x] **The same message ID on every attempt.** Consumers' Inboxes make a repeat harmless.
- [x] **The trace continues:** `traceparent`, workflow, correlation and causation IDs and the initiator are copied into Kafka headers.

## 2. Security

- [x] **Each relay has its own Kafka user and may write only its own topics** (ACLs; see 12).
- [x] **The separate CO relay's database login may only `SELECT` and `UPDATE` `outbox_messages`** (see 14).
- [x] **Kafka authentication fails closed:** without SASL settings the relay does not start. → `src/AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs`

## 3. Operations

- [x] **Readiness turns Degraded** while any row is parked or a pending one is overdue. → `src/Common/Observability/OutboxBacklogHealthCheck.cs`
- [x] **Liveness follows the loop's heartbeat:** a stopped loop is restarted.
- [x] **Payments: parked messages can be resent by an operator** with `workflow.reprocess`, recent MFA and a reason. The same message ID is kept, and the resend is recorded on the timeline.
- [x] **Accounts, Compliance and Partner Products: the same resend** from the Operations Platform Health page, through the Operations Journey API (the operator's token exchanged; the Domain API accepts only that delegated token, with `workflow.reprocess`, recent MFA and a reason). One atomic `UPDATE` resends only a message that is still parked. → `Controllers/OutboxController.cs`, `Infrastructure/Messaging/ParkedOutbox.cs`
- [ ] **The same resend for Customer Onboarding's relay and KYC's Outbox.**