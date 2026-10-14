# 11 — Kafka subscribers

Deployables: `src/AsyncWorkflows/Subscribers/<Context>/<Name>` (8 workers), the Audit API's in-process trail subscriber · Shared loop: `src/AsyncWorkflows/Infrastructure/Subscribers`

## 1. Reliability

- [x] **The offset is committed only after processing** (or after dead-lettering). *Because* a crash then means redelivery, never loss.
- [x] **Transient failures are retried in place:** refused identity (401 / 403), time-out (408), throttling (429), a server error (5xx) or an open circuit. Payments also retries a 409 race. *Because* partition order is kept.
- [x] **Bad messages go to the worker's own dead-letter topic:** malformed JSON, no `MessageId`, an unexpected event type, or another 4xx. *Because* one poison message must not stop the partition.
- [x] **Redelivery is harmless:** the receiving API records the message ID in its Inbox, in the same transaction.
- [x] **Retrying the POST is safe only because the endpoint is idempotent.** Each call has a 10 s timeout, jittered retries and a circuit breaker. → each `Program.cs` (`AddStandardResilienceHandler`)
- [x] **The trace continues** from the Kafka `traceparent` into the API call.

## 2. Identity

- [x] **Each worker has its OWN machine client:** client credentials and one write scope. Its API pins the `client_id` on its internal endpoint, and rate-limits that endpoint by the machine client's budget (3,000 a minute, per instance). A 429 is retried in place, so a backlog only slows down. → `src/IDP/ConfigRegistration/Clients/M2M/`
- [x] **The token is cached until shortly before it expires.** A 401 invalidates it, and the call is retried once, never in a loop. → `CachedM2MTokenClient.cs`
- [x] **Each worker has its own Kafka user:** it may read only its topics and its group, and write only its dead-letter topic (see 12).
- [x] **Secrets are read from configuration and fail closed:** the client secret and the Kafka password; the worker does not start without them. `appsettings.Development.json` holds Development values only.

## 3. Behaviour

- [x] **A thin worker:** it parses, validates and forwards to one API. Business rules stay in the API.
- [x] **The KYC Case Opening subscriber still opens a case when the initiator is missing,** but warns: separation of duties will then deny every decision on it (fail closed).
- [x] **No full account numbers in logs** (`LogMasking`).

## 4. Operations

- [x] **Each worker serves liveness and readiness on its own port** (`Health:Urls`). Readiness turns Degraded while a message is being retried. → `SubscriberHealth.cs`
- [x] **A new consumer group starts at the end of its topic** (`Setup-KafkaSecurity.ps1`). *Because* a brand-new group must not replay every retained message.

## 5. Production (open)

- [ ] **Secret store.** Secrets come from a secret store (R-10).
- [ ] **Dead-letter monitoring.** An alert fires on any message in a dead-letter topic.