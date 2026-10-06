# AccountsCommandSubscriber

Kafka subscriber **owned by the Accounts (ACC) bounded context**. It is the courier that carries the **Payments saga orchestrator's commands** to the Accounts API: reserve, settle (debit) or release a payment's funds. It decides nothing: Accounts applies each command to its own data and answers through its own Outbox on `accounts.funds.replies`. Business requirements: [Accounts-Requirements.md](../../../../Microservices/Accounts/doc/Accounts-Requirements.md). Contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
accounts.commands  (ReserveFunds / SettleFunds / ReleaseFunds, key = PaymentRef)
        │  (consumer group: accounts.command-subscriber; Kafka user: ewp-accounts-command-subscriber)
        ▼
validate envelope ── invalid ──► accounts.command-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached) ─► POST /internal/v1/accounts/funds/commands   ◄─ timeout, retry + jitter, circuit breaker
        │
        ▼
Accounts API: Inbox + lock hold and account + change + reply in the Outbox  ── one transaction
        │
        ▼
commit Kafka offset            (the reply reaches the orchestrator through the PaymentsSagaReplySubscriber)
```

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Redelivery, or the orchestrator resending after a timeout | Harmless: the Accounts API records each `MessageId` in its Inbox, and keeps one funds hold per `PaymentRef`; a repeated command repeats the reply. |
| Accounts API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** Retried in place with back-off; never skipped, partition order kept. |
| Malformed message, unknown command, missing fields, or 400 / 409 (e.g. settle what was released) | **Permanent.** Dead-lettered to `accounts.command-subscriber.dlq`. |

Stopping this worker is the easiest way to watch the saga's **timeouts**: the orchestrator resends the command with a growing timeout, and continues as soon as the worker is back.

## Configuration

Section `AccountsCommandSubscriber`; the client secret comes from configuration only (`AccountsCommandSubscriber:ClientSecret`). The Accounts API accepts this client only (policy `AccountsCommandSubscriberWrite`, pinned by `client_id`). Health: `http://localhost:5108/health/live` and `/health/ready`.