# CustomerOnboardingKycSubscriber

Kafka subscriber **owned by the Customer Onboarding bounded context**. It records Customer KYC outcomes on the onboarding application, which is the return path of the onboarding choreography. Business requirements: [CustomerOnboarding-Requirements.md](../../../../Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
kyc.case.created / kyc.case.approved / kyc.case.rejected
        │  (consumer group: customer-onboarding-kyc-subscriber)
        ▼
validate message ── invalid ──► customer-onboarding.kyc-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/onboarding/applications/{applicationId}/kyc-outcomes      ◄─ resilience pipeline:
        │   headers: X-Workflow-Id, X-Correlation-Id, X-Causation-Id (= KYC MessageId),   timeout, retry + jitter,
        │            X-Initiated-By-User-Id                                               circuit breaker
        ▼
Customer Onboarding API: Inbox check ─► aggregate transition ─► Outbox (StatusChanged)  ── one transaction
        │
        ▼
commit Kafka offset
```

The worker holds no business rules. Customer Onboarding's `OnboardingApplication` aggregate decides what each KYC fact means (`RecordKycCaseOpened` / `RecordKycApproved` / `RecordKycRejected`), and tolerates repeated or out-of-order facts.

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Several instances running | They share one consumer group. Kafka assigns each partition to one instance, so no two process the same message at the same time. Extra instances are hot standbys while topics have 1 partition. |
| Redelivery (crash before commit, rebalance) | Harmless: the CO API's Inbox (`inbox_messages`, unique `(message_id, consumer)`) records each KYC `MessageId` in the same transaction as its effect. A repeat returns `Duplicate`. |
| CO API / IDP unavailable, 5xx, timeout, open circuit | **Transient.** The message is retried in place (the consumer seeks back to it) with back-off of 2 s up to 60 s. It is never skipped, and partition order is kept. |
| Malformed JSON, unknown event type, no `ApplicationRef` / `ApplicationNumber`, 4xx (e.g. 404 unknown application — such as an event from before a database was recreated — or 409 when the referenced application has a different number) | **Permanent.** The message is copied to the dead-letter topic with `dlq-*` headers (reason, original topic/partition/offset, consumer group, time), then committed. The worker keeps running. |

## Configuration

Section `CustomerOnboardingKycSubscriber` (see `Configuration/KycSubscriberOptions.cs`): consumer group, dead-letter topic, IDP authority, client ID, scope, CO API URL and transient back-off. The topics are fixed in code.

The **client secret** comes from configuration only (`CustomerOnboardingKycSubscriber:ClientSecret`). The Development value is in `appsettings.Development.json`, and the `CustomerOnboardingKycSubscriber` launch profile sets `DOTNET_ENVIRONMENT=Development`. The worker refuses to start without it.

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\CustomerOnboardingKycSubscriber.csproj --launch-profile CustomerOnboardingKycSubscriber
```

The topic `customer-onboarding.kyc-subscriber.dlq` must exist (ReadMe.txt §3f).
