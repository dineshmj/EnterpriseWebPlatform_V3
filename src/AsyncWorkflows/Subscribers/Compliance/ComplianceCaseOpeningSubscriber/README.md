# ComplianceCaseOpeningSubscriber

Kafka subscriber **owned by the Compliance bounded context**: it is deployed and versioned with Compliance, even though it lives under `src/AsyncWorkflows`. It opens the Compliance case for each application whose KYC case was approved. Business requirements: [Compliance-Requirements.md](../../../../Microservices/Compliance/doc/Compliance-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
kyc.case.approved
        │  (consumer group: compliance.case-opening-subscriber; Kafka user: ewp-compliance-case-opening-subscriber)
        ▼
validate envelope ── invalid ──► compliance.case-opening-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/compliance/cases/from-kyc-approved         ◄─ resilience pipeline:
        │   body: ApplicationRef, ApplicationNumber,                timeout, retry + jitter,
        │         CustomerNumber, KycCaseId, BranchCode,            circuit breaker
        │         InitiatedByUserId, both KYC stage deciders,
        │         WorkflowId, CorrelationId, CausationId (= this MessageId)
        ▼
Compliance API: case exists? ─► Inbox row + ComplianceCase.Open + Outbox (ComplianceCaseCreated)  ── one transaction
        │
        ▼
commit Kafka offset            (screening then happens asynchronously inside the Compliance API)
```

The worker holds no Compliance business rules: the `ComplianceCase` aggregate does. It is a thin adapter on the shared reliable consume loop (`AsyncWorkflows.Infrastructure.Subscribers`) and supplies only `ComplianceCaseOpeningProcessor` (parse, call, classify).

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Several instances running | They share one consumer group; Kafka assigns each partition to one instance. |
| Redelivery, or the same approval published twice | Harmless. The Compliance API records each `MessageId` in its Inbox (consumer `compliance.case-opening`) in the same transaction as the new case, and keeps one case per `ApplicationRef`. |
| Compliance API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** Retried in place with back-off; never skipped, partition order kept. A 401 first refreshes the cached token once. |
| Malformed JSON, wrong event type, no `MessageId`, missing `ApplicationRef` / `ApplicationNumber` / `CustomerNumber` / `KycCaseId` / `BranchCode`, or 4xx | **Permanent.** Copied to `compliance.case-opening-subscriber.dlq` with `dlq-*` headers, then committed. |

`kyc.case.approved` messages published before increment 2a lack `BranchCode` and the KYC stage deciders, so they are dead-lettered by design. A missing initiator or stage decider is passed on as unknown; separation of duties then fails closed for an unknown initiator.

## Configuration

Section `ComplianceCaseOpeningSubscriber` (see `Configuration/ComplianceCaseOpeningSubscriberOptions.cs`): consumer group, dead-letter topic, back-off, IDP authority, client ID, scope (`compliance.write`) and Compliance API URL. The topic is fixed in code.

The **client secret** comes from configuration only (`ComplianceCaseOpeningSubscriber:ClientSecret`; elsewhere `ComplianceCaseOpeningSubscriber__ClientSecret`). The worker refuses to start without it. The Development value is in `appsettings.Development.json`, and the launch profile sets `DOTNET_ENVIRONMENT=Development`. The Compliance API accepts this client only (policy `ComplianceCaseOpeningSubscriberWrite`, pinned by `client_id`).

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\ComplianceCaseOpeningSubscriber.csproj --launch-profile ComplianceCaseOpeningSubscriber
```

The DLQ topic and the worker's Kafka user and ACLs are set up by [Setup-KafkaSecurity.ps1](../../../../../kafka/README.md).

## Health

`http://localhost:5104/health/live` (the consume loop runs) and `/health/ready` (joined the consumer group; *Degraded* while a message is retried in place).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-compliance-case-opening-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`compliance.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [ComplianceCaseOpeningSubscriberToComplianceApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/ComplianceCaseOpeningSubscriberToComplianceApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `ComplianceCaseOpeningSubscriberWrite` in [Program.cs](../../../../Microservices/Compliance/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [ComplianceCaseOpeningSubscriberOptions.cs](Configuration/ComplianceCaseOpeningSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |