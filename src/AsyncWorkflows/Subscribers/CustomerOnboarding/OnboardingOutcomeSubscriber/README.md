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