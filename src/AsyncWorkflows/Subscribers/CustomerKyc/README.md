# CustomerKycSubscriber

Kafka subscriber for the Customer KYC bounded context.

## Current responsibility

This Worker:
1. Consumes `customer.created` from Kafka.
2. Deserializes `CustomerCreatedIntegrationEvent`.
3. Logs the received event.
4. Commits the Kafka offset only after successful message handling.

The KYC API invocation is intentionally a placeholder because the Customer KYC Microservice has not yet been implemented.

## Not implemented yet

- Inbox/idempotency persistence
- M2M client-credentials authentication
- Customer KYC API invocation
- retry/dead-letter handling
- OpenTelemetry/tracing
- Kafka headers/correlation/causation propagation

These are deliberate follow-up slices.

## Kafka consumer group

`customer-kyc-subscriber`
