# CustomerKycSubscriber

Kafka subscriber **owned by the Customer KYC bounded context**: it is deployed and versioned with KYC, even though it lives under `src/AsyncWorkflows`. Business requirements: [CustomerKyc-Requirements.md](../../../Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md). Topic contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

## Current flow

```text
Kafka onboarding.application.submitted
        |
        v
Deserialize / validate event (ApplicationId, ApplicationNumber, CustomerNumber)
        |
        v
Acquire M2M access token
        |
        v
POST /internal/v1/kyc/cases/from-application-submitted
        |
        v
Customer KYC API
        |
        +--> idempotent KYC case creation (one case per application)
        +--> KYC business state
        +--> KYC Outbox event
        |
        v
Kafka offset commit
```

The subscriber is a workflow adapter. It does not contain KYC business rules.

## Authentication

The worker uses OAuth 2.0 Client Credentials against the IdentityServer authority and API/client settings defined centrally by the Landscape project.

The defaults for the IDP authority, client ID, scope and KYC API URL come from `Common.Landscape`. They can be overridden in the `CustomerKycSubscriber` configuration section, as can the topic, consumer group, retry settings and timeout.

The **client secret** is configuration only (`CustomerKycSubscriber:ClientSecret`); there is no compiled-in default, and the worker refuses to start without it. The Development value is in `appsettings.Development.json`, and the `CustomerKycSubscriber` launch profile sets `DOTNET_ENVIRONMENT=Development`. Elsewhere supply it from the environment or a secret store.

## Debug points

The implementation deliberately marks three useful debugging locations:

1. `CustomerKycSubscriberHostedService.HandleMessageAsync` — Kafka event received and parsed.
2. `M2MTokenClient.GetAccessTokenAsync` — immediately before the Client Credentials token request.
3. `CustomerKycApiClient.CallAsync` — immediately before the authenticated KYC API request.

## Delivery behavior

Kafka auto-commit is disabled. The consumer commits the Kafka offset only after the handler completes successfully.

Transient KYC API failures (408, 429 and 5xx) receive bounded exponential retries (`MaxAttempts`, `InitialRetryDelayMilliseconds`). Non-transient HTTP failures are surfaced immediately.

**Current failure behaviour.** A message that still fails after the retries is not committed. The exception escapes the hosted service, which **stops the worker process**. On restart, Kafka redelivers the same message, so one unprocessable ("poison") message blocks the topic until it is fixed or skipped by hand. A dead-letter topic is planned to replace this.

Other current limitations:

- The M2M token is requested for every message rather than cached until expiry.
- There is no Inbox. Idempotency relies on the KYC API's unique `customer_number` constraint.
- The `X-Workflow-Message-Id` header is sent, but the KYC API does not read it yet.

## Event compatibility

The subscriber reads the standard envelope (workflow metadata at the top level, the event under `Payload`) as a tolerant reader: unknown fields are ignored. `InitiatedByUserId` is forwarded to the KYC API; if it is absent the KYC API receives `null` and the subscriber logs a warning. Events published before `CustomerNumber` was added to `OnboardingApplicationSubmitted` are rejected as invalid. Recreate the topics when you recreate the databases (ReadMe.txt §3f).