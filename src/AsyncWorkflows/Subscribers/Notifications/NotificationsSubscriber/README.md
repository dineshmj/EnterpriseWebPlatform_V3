# NotificationsSubscriber

Kafka subscriber **owned by the Notifications context**. It reads the workflow's new-work and outcome topics and hands each event, unchanged, to the [Notifications API](../../../../Microservices/Notifications/API/README.md), which decides who is told what. The worker holds no notification rules.

## Flow

```text
kyc.case.created / approved / rejected
compliance.case.screened / approved / rejected
accounts.application.created / rejected, accounts.account.opened / opening.failed
payments.payment.events (PaymentApprovalRequired, PaymentCompleted / Rejected / Failed / CompensationFailed)
        │  (consumer group: notifications.subscriber; Kafka user: ewp-notifications-subscriber)
        ▼
validate (MessageId, EventType) ── invalid ──► notifications.subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached) ─► POST /internal/v1/notifications/events   ◄─ timeout, retry + jitter, circuit breaker
        │
        ▼
commit Kafka offset
```

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Redelivery | Harmless: the API records each `MessageId` in its Inbox, and one event tells each audience at most once. |
| An event that tells nobody | Processed; the Inbox records it. |
| Notifications API / IDP unavailable, 5xx, timeout, 401 / 403 | Transient: retried in place with back-off; never skipped. |
| Malformed message, or the API answers 4xx | Dead-lettered to `notifications.subscriber.dlq` with diagnostic headers. |

The consumer group starts at the **latest** offsets (`ps\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare`): notifications are about what happens from now on, never a replay of history.

Health: `http://localhost:5107/health/live` and `/health/ready`. Secrets: `Kafka:SaslPassword` and `NotificationsSubscriber:ClientSecret` (Development values in `appsettings.Development.json`).