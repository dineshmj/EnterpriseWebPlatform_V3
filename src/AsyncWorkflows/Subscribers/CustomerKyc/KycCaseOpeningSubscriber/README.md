# KycCaseOpeningSubscriber

Kafka subscriber **owned by the Customer KYC bounded context**: it is deployed and versioned with KYC, even though it lives under `src/AsyncWorkflows`. It opens the KYC case for each submitted onboarding application. Business requirements: [CustomerKyc-Requirements.md](../../../../Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
onboarding.application.submitted
        │  (consumer group: customer-kyc.case-opening-subscriber; Kafka user: ewp-kyc-case-opening-subscriber)
        ▼
validate envelope ── invalid ──► customer-kyc.case-opening-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/kyc/cases/from-application-submitted       ◄─ resilience pipeline:
        │   body: ApplicationRef, ApplicationNumber,                timeout, retry + jitter,
        │         CustomerNumber, BranchCode, InitiatedByUserId,    circuit breaker
        │         WorkflowId, CorrelationId, CausationId (= this MessageId)
        ▼
Customer KYC API: case exists? ─► Inbox row + KycCase.Open + Outbox (KycCaseCreated)  ── one transaction
        │
        ▼
commit Kafka offset
```

The worker holds no KYC business rules: the `KycCase` aggregate does. It is a thin adapter on the shared reliable consume loop (`AsyncWorkflows.Infrastructure.Subscribers`), which the Onboarding Outcome Subscriber uses too. This worker supplies only `KycCaseOpeningProcessor` (parse, call, classify).

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Several instances running | They share one consumer group. Kafka assigns each partition to one instance, so no two process the same message at the same time. |
| Redelivery (crash before commit, rebalance), or the same application published twice | Harmless. The KYC API records each `MessageId` in its Inbox (`inbox_messages`, unique `(message_id, consumer)`) in the same transaction as the new case. It also keeps one case per `ApplicationRef`, so it returns the existing case (200) instead of opening another. |
| KYC API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** The message is retried in place (the consumer seeks back to it) with back-off from 2 s up to 60 s. It is never skipped, and partition order is kept. A 401 first refreshes the cached token once. A 401 or 403 that persists means this worker's identity is not accepted: a configuration problem, so messages wait instead of being dead-lettered. |
| Malformed JSON, wrong event type, no `MessageId`, missing `ApplicationRef` / `ApplicationNumber` / `CustomerNumber` / `BranchCode`, or 4xx (e.g. 400 for an invalid branch code) | **Permanent.** The message is copied to `customer-kyc.case-opening-subscriber.dlq` with `dlq-*` headers (reason, original topic/partition/offset, consumer group, time), then committed. The worker keeps running. |

A missing `InitiatedByUserId` is not rejected, but it is logged as a warning. Separation of duties fails closed, so every decision on such a case will be denied.

## Configuration

Section `KycCaseOpeningSubscriber` (see `Configuration/KycCaseOpeningSubscriberOptions.cs`): consumer group, dead-letter topic, transient back-off, IDP authority, client ID, scope and KYC API URL. The topic is fixed in code. The defaults come from `Common.Landscape`.

The **client secret** comes from configuration only (`KycCaseOpeningSubscriber:ClientSecret`; elsewhere `KycCaseOpeningSubscriber__ClientSecret` from the environment or a secret store). There is no compiled-in default, and the worker refuses to start without it. The Development value is in `appsettings.Development.json`, and the `KycCaseOpeningSubscriber` launch profile sets `DOTNET_ENVIRONMENT=Development`.

## Debug points

1. `KycCaseOpeningProcessor.ProcessAsync`: the event has been received and validated.
2. `KycCaseOpeningProcessor.SendAsync`: immediately before the authenticated call to the KYC API.

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\KycCaseOpeningSubscriber.csproj --launch-profile KycCaseOpeningSubscriber
```

The topic `customer-kyc.case-opening-subscriber.dlq` must exist, and the worker's Kafka user needs its ACLs: both are set up by [Setup-KafkaSecurity.ps1](../../../../../kafka/README.md).

## Health

`http://localhost:5102/health/live` (the consume loop runs) and `/health/ready` (joined the consumer group; *Degraded* while a message is retried in place).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-kyc-case-opening-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`customer-kyc.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [KycCaseOpeningSubscriberToCustomerKycApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/KycCaseOpeningSubscriberToCustomerKycApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `KycCaseOpeningSubscriberWrite` in [Program.cs](../../../../Microservices/CustomerKyc/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [KycCaseOpeningSubscriberOptions.cs](Configuration/KycCaseOpeningSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |