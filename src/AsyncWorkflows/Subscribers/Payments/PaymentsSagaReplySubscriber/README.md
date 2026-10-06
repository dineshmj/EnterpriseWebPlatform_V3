# PaymentsSagaReplySubscriber

Kafka subscriber **owned by the Payments bounded context**. It is the courier that brings **Accounts' replies** back to the payment saga orchestrator in the Payments API. It decides nothing: the `PaymentSaga` decides what each reply means (and ignores a late or duplicated one). Requirements: [Payments-Requirements.md](../../../../Microservices/Payments/doc/Payments-Requirements.md). Contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
accounts.funds.replies  (FundsReserved / FundsReservationFailed / FundsSettled / FundsReleased, key = PaymentRef)
        │  (consumer group: payments.saga-reply-subscriber; Kafka user: ewp-payments-saga-reply-subscriber)
        ▼
validate envelope ── invalid ──► payments.saga-reply-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached) ─► POST /internal/v1/payment-sagas/replies   ◄─ timeout, retry + jitter, circuit breaker
        │
        ▼
Payments API: Inbox + lock the saga + saga decides + next command in the Outbox  ── one transaction
        │
        ▼
commit Kafka offset
```

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Redelivery | Harmless: the Payments API records each `MessageId` in its Inbox; a reply the saga no longer waits for is recorded in the timeline as REPLY_IGNORED. |
| Payments API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** Retried in place with back-off. Meanwhile the saga's own timer may resend the command - that is safe. |
| Malformed message, unknown reply, missing PaymentRef, or 4xx (e.g. unknown payment) | **Permanent.** Dead-lettered to `payments.saga-reply-subscriber.dlq`. |

## Configuration

Section `PaymentsSagaReplySubscriber`; the client secret comes from configuration only (`PaymentsSagaReplySubscriber:ClientSecret`). The Payments API accepts this client only (policy `PaymentsSagaReplySubscriberWrite`, pinned by `client_id`). Health: `http://localhost:5109/health/live` and `/health/ready`.