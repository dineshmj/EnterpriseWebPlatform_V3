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

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-notifications-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`notifications.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [NotificationsSubscriberToNotificationsApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/NotificationsSubscriberToNotificationsApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `NotificationsSubscriberWrite` in [Program.cs](../../../../Microservices/Notifications/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [NotificationsSubscriberOptions.cs](Configuration/NotificationsSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |