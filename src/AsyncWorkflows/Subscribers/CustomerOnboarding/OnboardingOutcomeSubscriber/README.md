# OnboardingOutcomeSubscriber

Kafka subscriber **owned by the Customer Onboarding bounded context**. It records Customer KYC, Compliance and Accounts outcomes on the onboarding application, which is the return path of the onboarding choreography. Business requirements: [CustomerOnboarding-Requirements.md](../../../../Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
kyc.case.created / kyc.case.approved / kyc.case.rejected
compliance.case.created / compliance.case.approved / compliance.case.rejected
accounts.application.created / accounts.account.opened / accounts.application.rejected
        │  (consumer group: customer-onboarding.outcome-subscriber; Kafka user: ewp-onboarding-outcome-subscriber)
        ▼
validate message ── invalid ──► customer-onboarding.outcome-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/onboarding/applications/{applicationRef}/kyc-outcomes         ◄─ resilience pipeline:
     (or .../compliance-outcomes for compliance.*, .../account-outcomes for accounts.* events)
        │   headers: X-Workflow-Id, X-Correlation-Id, X-Causation-Id (= event MessageId),   timeout, retry + jitter,
        │            X-Initiated-By-User-Id                                               circuit breaker
        ▼
Customer Onboarding API: Inbox check ─► aggregate transition ─► Outbox (StatusChanged)  ── one transaction
        │
        ▼
commit Kafka offset
```

The worker is a thin adapter on the shared reliable consume loop (`AsyncWorkflows.Infrastructure.Subscribers`, also used by the KYC Case Opening Subscriber); it supplies only `OnboardingOutcomeProcessor`, which routes each event type to its endpoint. It holds no business rules. Customer Onboarding's `OnboardingApplication` aggregate decides what each fact means (`RecordKycCaseOpened` / `RecordKycApproved` / `RecordKycRejected`, `RecordComplianceCaseOpened` / `RecordComplianceApproved` / `RecordComplianceRejected`, `RecordAccountApplicationCreated` / `RecordAccountOpened` / `RecordAccountApplicationRejected`), and tolerates repeated or out-of-order facts.

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Several instances running | They share one consumer group. Kafka assigns each partition to one instance, so no two process the same message at the same time. Extra instances are hot standbys while topics have 1 partition. |
| Redelivery (crash before commit, rebalance) | Harmless: the CO API's Inbox (`inbox_messages`, unique `(message_id, consumer)`) records each KYC `MessageId` in the same transaction as its effect. A repeat returns `Duplicate`. |
| CO API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 (this worker's identity not accepted: a configuration problem, not a bad message) | **Transient.** The message is retried in place (the consumer seeks back to it) with back-off of 2 s up to 60 s. It is never skipped, and partition order is kept. |
| Malformed JSON, unknown event type, no `ApplicationRef` / `ApplicationNumber`, 4xx (e.g. 404 unknown application — such as an event from before a database was recreated — or 409 when the referenced application has a different number) | **Permanent.** The message is copied to the dead-letter topic with `dlq-*` headers (reason, original topic/partition/offset, consumer group, time), then committed. The worker keeps running. |

## Configuration

Section `OnboardingOutcomeSubscriber` (see `Configuration/OnboardingOutcomeSubscriberOptions.cs`): consumer group, dead-letter topic, IDP authority, client ID, scope, CO API URL and transient back-off. The topics are fixed in code.

The **client secret** comes from configuration only (`OnboardingOutcomeSubscriber:ClientSecret`). The Development value is in `appsettings.Development.json`, and the `OnboardingOutcomeSubscriber` launch profile sets `DOTNET_ENVIRONMENT=Development`. The worker refuses to start without it.

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\OnboardingOutcomeSubscriber.csproj --launch-profile OnboardingOutcomeSubscriber
```

The topic `customer-onboarding.outcome-subscriber.dlq` must exist, and the worker's Kafka user needs its ACLs: both are set up by [Setup-KafkaSecurity.ps1](../../../../../kafka/README.md).

## Health

`http://localhost:5103/health/live` (the consume loop runs) and `/health/ready` (joined the consumer group; *Degraded* while a message is retried in place).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-onboarding-outcome-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`customer-onboarding.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `OnboardingOutcomeSubscriberWrite` in [Program.cs](../../../../Microservices/CustomerOnboarding/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [OnboardingOutcomeSubscriberOptions.cs](Configuration/OnboardingOutcomeSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |