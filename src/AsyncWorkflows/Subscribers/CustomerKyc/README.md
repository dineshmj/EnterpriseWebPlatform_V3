# CustomerKycSubscriber

Kafka subscriber for the Customer KYC bounded context.

## Current flow

```text
Kafka customer.created
        |
        v
Deserialize / validate event
        |
        v
Acquire M2M access token
        |
        v
POST /internal/v1/kyc/cases/from-customer-created
        |
        v
Customer KYC API
        |
        +--> idempotent KYC case creation
        +--> KYC business state
        +--> KYC Outbox event
        |
        v
Kafka offset commit
```

The subscriber is a workflow adapter. It does not contain KYC business rules.

## Authentication

The worker uses OAuth 2.0 Client Credentials against the IdentityServer authority and API/client settings defined centrally by the Landscape project.

The subscriber does **not** hard-code the M2M client ID, client secret, IdentityServer authority, scope, or Customer KYC API URL in its own configuration. These values are obtained from:

- `EnterpriseWebPlatform.Common.Landscape.IDP.AUTHORITY`
- `CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M`
- `CustomerKycMicroservice.CLIENT_SECRET_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M`
- `CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE`
- `CustomerKycMicroservice.MICROSERVICE_API_BASE_URL`

The remaining operational settings (topic, consumer group, retry settings and timeout) remain configurable in `appsettings.json`.

For a real deployment, move secrets out of source-controlled configuration/compiled constants into a secret store.

## Debug points

The implementation deliberately marks three useful debugging locations:

1. `CustomerKycSubscriberHostedService.HandleMessageAsync` — Kafka event received and parsed.
2. `M2MTokenClient.GetAccessTokenAsync` — immediately before the Client Credentials token request.
3. `CustomerKycApiClient.CallAsync` — immediately before the authenticated KYC API request.

## Delivery behavior

Kafka auto-commit is disabled. The consumer commits the Kafka offset only after the handler completes successfully.

Transient KYC API failures (408, 429 and 5xx) receive bounded exponential retries. Non-transient HTTP failures are surfaced immediately. A message that ultimately fails is therefore not committed and can be redelivered by Kafka.

Inbox persistence is not implemented in this slice. The KYC API itself is idempotent for a customer through its unique customer-number constraint, and the next implementation slice can add a formal Inbox/ProcessedMessages store to the subscriber.

## Event compatibility

The subscriber understands the current V3 envelope shape containing `Payload`, and also accepts the older direct-event shape. When `InitiatedByUserId` is present in the envelope or payload it is forwarded to the KYC API. If it is absent, the KYC API receives `null` and the subscriber logs a warning.
