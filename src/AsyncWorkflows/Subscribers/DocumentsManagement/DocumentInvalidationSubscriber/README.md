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

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own Kafka user `ewp-dm-invalidation-subscriber` (SCRAM-SHA-512); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`documents-management.write`), for this caller–callee pair only | A shared service account would let every worker call every API | Privilege creep; one leak opening everything | [DocumentInvalidationSubscriberToDocumentsManagementApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/DocumentInvalidationSubscriberToDocumentsManagementApiM2M.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `DocumentInvalidationSubscriberWrite` in [Program.cs](../../../../Microservices/DocumentsManagement/API/Program.cs) |
| 4 | Secrets have no defaults; options are validated at start-up (fail closed) | The worker could start with an empty or default secret | Misconfiguration becoming a silent security gap | [DocumentInvalidationSubscriberOptions.cs](Configuration/DocumentInvalidationSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |