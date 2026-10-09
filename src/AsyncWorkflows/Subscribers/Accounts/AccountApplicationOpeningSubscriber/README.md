# AccountApplicationOpeningSubscriber

Kafka subscriber **owned by the Accounts (ACC) bounded context**: it is deployed and versioned with Accounts, even though it lives under `src/AsyncWorkflows`. It opens the account application for each onboarding application that Compliance approved — the last step of the onboarding saga. Business requirements: [Accounts-Requirements.md](../../../../Microservices/Accounts/doc/Accounts-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
compliance.case.approved
        │  (consumer group: accounts.application-opening-subscriber; Kafka user: ewp-accounts-application-opening-subscriber)
        ▼
validate envelope ── invalid ──► accounts.application-opening-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/accounts/applications/from-compliance-approved   ◄─ resilience pipeline:
        │   body: ApplicationRef, ApplicationNumber, CustomerNumber,        timeout, retry + jitter,
        │         ComplianceCaseId, BranchCode, InitiatedByUserId,          circuit breaker
        │         ComplianceApprovedByUserId (the CMP approver, for SoD),
        │         WorkflowId, CorrelationId, CausationId (= this MessageId)
        ▼
Accounts API: application exists? ─► Inbox row + AccountApplication.Open + Outbox (AccountApplicationCreated)  ── one transaction
        │
        ▼
commit Kafka offset            (an account officer decides it; core banking then opens the account)
```

The worker holds no account rules: the `AccountApplication` aggregate does. It is a thin adapter on the shared reliable consume loop (`AsyncWorkflows.Infrastructure.Subscribers`) and supplies only `AccountApplicationOpeningProcessor`.

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Several instances running | They share one consumer group; Kafka assigns each partition to one instance. |
| Redelivery, or the same approval published twice | Harmless. The Accounts API records each `MessageId` in its Inbox (consumer `accounts.application-opening`) in the same transaction as the new application, and keeps one application per `ApplicationRef`. |
| Accounts API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** Retried in place with back-off; never skipped, partition order kept. A 401 first refreshes the cached token once. |
| Malformed JSON, wrong event type, no `MessageId`, missing `ApplicationRef` / `ApplicationNumber` / `CustomerNumber` / `ComplianceCaseId` / `BranchCode`, or 4xx | **Permanent.** Copied to `accounts.application-opening-subscriber.dlq` with `dlq-*` headers, then committed. |

## Configuration

Section `AccountApplicationOpeningSubscriber` (see `Configuration/AccountApplicationOpeningSubscriberOptions.cs`): consumer group, dead-letter topic, back-off, IDP authority, client ID, scope (`accounts.write`) and Accounts API URL. The topic is fixed in code.

The **client secret** comes from configuration only (`AccountApplicationOpeningSubscriber:ClientSecret`; elsewhere `AccountApplicationOpeningSubscriber__ClientSecret`). The Accounts API accepts this client only (policy `AccountApplicationOpeningSubscriberWrite`, pinned by `client_id`).

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\AccountApplicationOpeningSubscriber.csproj --launch-profile AccountApplicationOpeningSubscriber
```

The DLQ topic and the worker's Kafka user and ACLs are set up by [Setup-KafkaSecurity.ps1](../../../../../kafka/README.md).

## Health

`http://localhost:5106/health/live` (the consume loop runs) and `/health/ready` (joined the consumer group; *Degraded* while a message is retried in place).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-accounts-application-opening-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`accounts.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [AccountApplicationOpeningSubscriberToAccountsApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/AccountApplicationOpeningSubscriberToAccountsApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `AccountApplicationOpeningSubscriberWrite` in [Program.cs](../../../../Microservices/Accounts/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [AccountApplicationOpeningSubscriberOptions.cs](Configuration/AccountApplicationOpeningSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |