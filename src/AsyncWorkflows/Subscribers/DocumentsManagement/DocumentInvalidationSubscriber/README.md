# DocumentInvalidationSubscriber

Kafka subscriber **owned by the Documents Management (DM) bounded context**, and DM's first. It keeps DM's evidence in step with the onboarding saga:

- **Submission** (`onboarding.application.submitted`): the application's evidence documents become **ATTACHED**. From then on they are KYC records and can no longer be deleted.
- **Rejection** (`onboarding.application.rejected`, by KYC, Compliance or Accounts): the saga's **compensation**. The evidence documents are marked **INVALIDATED**.

In both cases the documents are **retained, never deleted**, because audit and regulatory retention (AML/CTF record keeping) require them. The project keeps its original name; it now handles both topics through `DocumentEvidenceProcessor`. Business requirements: [DocumentsManagement-Requirements.md](../../../../Microservices/DocumentsManagement/doc/DocumentsManagement-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

## Flow

```text
CO submits ─► outbox "OnboardingApplicationSubmitted" (evidence document IDs) ─► same path, POST /internal/v1/documents/attachments ─► Document.Attach

KYC, Compliance or Accounts rejects ─► CO: application REJECTED + outbox "OnboardingApplicationRejected"
                              (names the evidence document IDs recorded at submission)
onboarding.application.rejected
        │  (consumer group: documents-management.invalidation-subscriber; Kafka user: ewp-dm-invalidation-subscriber)
        ▼
validate envelope ── invalid ──► documents-management.invalidation-subscriber.dlq  (+ headers), commit
        │
        ▼
M2M token (cached until expiry)
        │
        ▼
POST /internal/v1/documents/invalidations          ◄─ resilience pipeline:
        │   body: MessageId, ApplicationRef,            timeout, retry + jitter,
        │         ApplicationNumber, BranchCode,        circuit breaker
        │         RejectedBy, DocumentIds
        ▼
DM API: Inbox check ─► Document.Invalidate per document of the SAME branch ─► Inbox row   ── one transaction
        │
        ▼
commit Kafka offset
```

The worker holds no document rules: the `Document` aggregate does (idempotent invalidation, retention). It is a thin adapter on the shared reliable consume loop (`AsyncWorkflows.Infrastructure.Subscribers`) and supplies only `DocumentEvidenceProcessor`.

## Delivery guarantees

| Situation | Behaviour |
|---|---|
| Redelivery, or the same submission / rejection published twice | Harmless. DM records each `MessageId` in its Inbox (consumers `documents-management.attachment` and `documents-management.invalidation`) in the same transaction; attaching an attached document, or invalidating an invalidated one, changes nothing. |
| The rejection overtakes the submission | The documents are INVALIDATED; the late submission leaves them INVALIDATED (evidence is never revived). |
| A named document does not exist (e.g. DM's database was recreated) | Counted as *not found*; the message is still processed. |
| A named document belongs to another branch | **Not** invalidated (an event never reaches across branches) and logged as a warning. |
| No evidence documents in the event (an application submitted before evidence was recorded) | Processed as "nothing to do"; the Inbox records it. |
| DM API / IDP unavailable, 5xx, timeout, open circuit, 401 / 403 | **Transient.** Retried in place with back-off; never skipped, partition order kept. |
| Malformed JSON, wrong event type, no `MessageId`, missing `ApplicationRef` / `ApplicationNumber` / `BranchCode` / `RejectedBy`, or 4xx | **Permanent.** Copied to `documents-management.invalidation-subscriber.dlq` with `dlq-*` headers, then committed. |

## Configuration

Section `DocumentInvalidationSubscriber` (see `Configuration/DocumentInvalidationSubscriberOptions.cs`): consumer group, dead-letter topic, back-off, IDP authority, client ID, scope (`documents-management.write`) and DM API URL. The topic is fixed in code.

The **client secret** comes from configuration only (`DocumentInvalidationSubscriber:ClientSecret`; elsewhere `DocumentInvalidationSubscriber__ClientSecret`). The DM API accepts this client only on its invalidation endpoint (policy `DocumentInvalidationSubscriberWrite`, pinned by `client_id`).

## Run

Part of the solution's multi-project launch profile. To run it alone:

```powershell
dotnet run --project .\DocumentInvalidationSubscriber.csproj --launch-profile DocumentInvalidationSubscriber
```

The topic, DLQ, Kafka user and ACLs are set up by [Setup-KafkaSecurity.ps1](../../../../../kafka/README.md).

## Health

`http://localhost:5105/health/live` (the consume loop runs) and `/health/ready` (joined the consumer group; *Degraded* while a message is retried in place).