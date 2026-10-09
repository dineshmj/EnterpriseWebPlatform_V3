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

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-accounts-command-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`accounts.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [AccountsCommandSubscriberToAccountsApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/AccountsCommandSubscriberToAccountsApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `AccountsCommandSubscriberWrite` in [Program.cs](../../../../Microservices/Accounts/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [AccountsCommandSubscriberOptions.cs](Configuration/AccountsCommandSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |