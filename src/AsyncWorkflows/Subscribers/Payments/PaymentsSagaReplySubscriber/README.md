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

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-payments-saga-reply-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`payments.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [PaymentsSagaReplySubscriberToPaymentsApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/PaymentsSagaReplySubscriberToPaymentsApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `PaymentsSagaReplySubscriberWrite` in [Program.cs](../../../../Microservices/Payments/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [PaymentsSagaReplySubscriberOptions.cs](Configuration/PaymentsSagaReplySubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |