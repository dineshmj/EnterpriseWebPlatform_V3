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