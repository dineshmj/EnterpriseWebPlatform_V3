# KycCaseOpeningSubscriber

Kafka subscriber **owned by the Customer KYC bounded context**: it is deployed and versioned with KYC, even though it lives under `src/AsyncWorkflows`. It opens the KYC case for each submitted onboarding application. Business requirements: [CustomerKyc-Requirements.md](../../../../Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
onboarding.application.submitted
        │  (consumer group: customer-kyc-subscriber)
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

The topic `customer-kyc.case-opening-subscriber.dlq` must exist (ReadMe.txt §3f).
