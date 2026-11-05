# ComplianceCaseOpeningSubscriber

Kafka subscriber **owned by the Compliance bounded context**: it is deployed and versioned with Compliance, even though it lives under `src/AsyncWorkflows`. It opens the Compliance case for each application whose KYC case was approved. Business requirements: [Compliance-Requirements.md](../../../../../doc/Requirements/Compliance-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../../doc/Integration-Event-Catalogue.md).

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

The worker proves itself to the IDP with **its own key** (private_key_jwt - no client secret exists), and every token it gets is **DPoP-bound** to a second key, with a new proof on each call: `ComplianceCaseOpeningSubscriber:ClientKey` and `ComplianceCaseOpeningSubscriber:DPoPKey` name the key files (encrypted PEM and a password file; elsewhere `ComplianceCaseOpeningSubscriber__ClientKey__Path` and `ComplianceCaseOpeningSubscriber__DPoPKey__Path` with their `PasswordPath`, mounted from a secret store). There is no compiled-in default, and the worker refuses to start without readable keys; the Development key files are made by [New-EwpLocalPki.ps1](../../../../../ps/pki/New-EwpLocalPki.ps1). The launch profile sets `DOTNET_ENVIRONMENT=Development`. The Compliance API accepts this client only (policy `ComplianceCaseOpeningSubscriberWrite`, pinned by `client_id`).

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
| 1 | Own Kafka user `ewp-compliance-case-opening-subscriber` (SCRAM-SHA-512 inside TLS: SASL_SSL, the broker's certificate and host name checked); ACLs let it read only its topics, use only its consumer group and write only its dead-letter topic | Any program on the network could read the events or publish fake ones | Event spoofing and eavesdropping on the bus | `Kafka:SaslUsername` in [appsettings.json](appsettings.json); [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1); [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |
| 2 | Own machine identity (client credentials) with one scope (`compliance.write`), for this caller–callee pair only; proved with its own key (private_key_jwt - no shared secret), and every token bound to its DPoP key with a new proof per call (RFC 9449) | A shared service account would let every worker call every API; a shared secret or a bearer token copied from a log or a proxy would let anyone act as the worker | Privilege creep; one leak opening everything; stolen-token replay | [ComplianceCaseOpeningSubscriberToComplianceApiM2M.cs](../../../../IDP/ConfigRegistration/Clients/M2M/ComplianceCaseOpeningSubscriberToComplianceApiM2M.cs), [MachineKeys.cs](../../../Infrastructure/Subscribers/MachineKeys.cs), [DPoPMachineTokenHandler.cs](../../../Infrastructure/Subscribers/DPoPMachineTokenHandler.cs) |
| 3 | The API accepts this worker's calls only from its pinned `client_id` | Another service with the scope could post the same internal call | Service impersonation | Policy `ComplianceCaseOpeningSubscriberWrite` in [Program.cs](../../../../Microservices/Compliance/API/Program.cs) |
| 4 | Key files and settings have no defaults; options are validated at start-up, the keys read (fail closed) | The worker could start with no key or a broken one and fail later, message by message | Misconfiguration becoming a silent security gap | [ComplianceCaseOpeningSubscriberOptions.cs](Configuration/ComplianceCaseOpeningSubscriberOptions.cs), [SubscriberServiceCollectionExtensions.cs](../../../Infrastructure/Subscribers/SubscriberServiceCollectionExtensions.cs) |
| 5 | A malformed or unknown message goes to the dead-letter topic with the reason, never into the API | A poison message would block the partition or reach the API half-understood | Denial of service by one bad message; processing of invalid input | [KafkaSubscriberHostedService.cs](../../../Infrastructure/Subscribers/KafkaSubscriberHostedService.cs), [Processing](Processing) |
| 6 | Bounded retries with back-off, jitter and a circuit breaker; retried only because the API is idempotent (Inbox) | A struggling API would be hammered; a retried call could act twice | Retry storms and duplicated effects | `AddStandardResilienceHandler` in [Program.cs](Program.cs) |
| 7 | The access token is cached until shortly before expiry | A token request per message would load the IDP (and fail with it) | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](../../../Infrastructure/Subscribers/CachedM2MTokenClient.cs) |
| 8 | Container image: built only by CI from a commit, chiseled non-root runtime (no shell, files read-only to the process), base images pinned by tag and digest, scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); the Kafka password comes from the environment, the worker's two keys and the Kafka trust root from mounted files - none of them is in the image. | A developer-built or tampered image could run in an environment; a credential in a layer stays in the registry for good; a shell in the image helps an attacker who gets in | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../../.dockerignore), [container-images.yml](../../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../../doc/Containers.md) |
| 9 | The erasure of a customer's personal data: the worker also reads `customer.personal-data.erasure`; on `CustomerPersonalDataErased` it asks Compliance's API to erase its copy and publishes `PersonalDataCopyErased` (`customer.personal-data.erasure.confirmed`) only after a 2xx - then commits. A crash or failed publish leaves the message for a retry (the API answers "done" again); a refusal is dead-lettered and never confirmed. The confirmation carries IDs and counts only; its Kafka user may write that topic and nothing new besides | A copy could be reported erased while it still exists, or an erasure lost between two steps | Repudiation; unproven erasure (GDPR Art. 5(2)) | [PersonalDataErasureForwarder.cs](../../../Infrastructure/Subscribers/PersonalDataErasureForwarder.cs), ACLs in [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1) |