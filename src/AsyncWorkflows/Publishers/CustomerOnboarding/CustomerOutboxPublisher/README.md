# CustomerOutboxPublisher

The Customer Onboarding Outbox relay: publishes committed Outbox rows to Kafka.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Own database user `ewp_customer_outbox_relay`: it may only read and update the Outbox table, not the customers | A compromised relay could read or change every customer record | Data exposure through the most network-facing worker | [EwpServiceDbUsers.sql](../../../../../db/EwpServiceDbUsers.sql) |
| 2 | Own Kafka user `ewp-co-outbox-relay`, allowed to write only Customer Onboarding's topics; it is the only writer of `onboarding.application.submitted` | Any component could publish a submission with a forged initiator | Bypassing separation of duties (the initiator travels on this event) | [Setup-KafkaSecurity.ps1](../../../../../ps/kafka/Setup-KafkaSecurity.ps1), `Kafka:SaslUsername` in [appsettings.json](appsettings.json) |
| 3 | Rows claimed with parameterised SQL (`FOR UPDATE SKIP LOCKED`), never string concatenation | A crafted value could change the query | SQL injection (OWASP A03) | [CustomerOutboxPublisher.cs](Publishing/CustomerOutboxPublisher.cs) |
| 4 | Idempotent producer, `acks=all`; a row failing ten times is parked for an operator, not retried forever | Events could be lost or duplicated; a bad row could loop endlessly | Lost or repeated business events; resource exhaustion | [KafkaProducer.cs](../../../Infrastructure/Kafka/KafkaProducer.cs), `MaxAttempts` in [CustomerOutboxPublisherOptions.cs](Configuration/CustomerOutboxPublisherOptions.cs) |
| 5 | Kafka credentials required for SASL; the relay refuses to start without them | It could fall back to an unauthenticated connection | Misconfiguration becoming a silent gap | [KafkaClientSecurity.cs](../../../Infrastructure/Kafka/KafkaClientSecurity.cs) |