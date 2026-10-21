# AsyncWorkflows.Infrastructure.Kafka

Shared Kafka plumbing: client security, producer and topic names.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | SASL/SCRAM-SHA-512 per component; a SASL protocol without username and password throws at start-up | A component could connect anonymously, or silently without the intended identity | Unauthenticated access to the bus | [KafkaClientSecurity.cs](KafkaClientSecurity.cs) |
| 1a | TLS to the broker (`SaslSsl`, also the default): the broker's certificate is checked against `Kafka:SslCaLocation` (local: the EWP trust root) and its host name is checked explicitly; a configured CA file that does not exist stops the start-up | Events, customer numbers and payment details readable by anyone watching the traffic; a fake broker could collect them | Eavesdropping and tampering on the bus; broker impersonation | [KafkaClientSecurity.cs](KafkaClientSecurity.cs), [KafkaOptions.cs](KafkaOptions.cs) |
| 2 | Idempotent producer with `acks=all` | A broker failover could lose or duplicate a message | Lost or repeated business events | [KafkaProducer.cs](KafkaProducer.cs) |
| 3 | Topic names in one place; topic auto-creation is off at the broker | A mistyped topic name would silently create a new, unprotected topic | Events leaking to a topic without ACLs | [KafkaTopicNames.cs](KafkaTopicNames.cs), [Setup-KafkaSecurity.ps1](../../../../ps/kafka/Setup-KafkaSecurity.ps1) |
| 4 | The Kafka library's own messages and errors go through the application's logger (`KafkaClientLog`, every producer and consumer): library chatter at Debug, each kind of error (code) as a Warning the first time, then at most every 5 minutes while it lasts, a fatal error as Critical | Library lines bypass Serilog (no level, no trace, never in the central log store) and, while the broker is down, two console lines every 2-3 s per service bury the real messages | Security-relevant events (refused sign-in to the broker, TLS failures) lost or unnoticed (OWASP A09) | [KafkaClientLog.cs](KafkaClientLog.cs) |

Local development uses `SASL_SSL` like production; the broker's certificate comes from `ps\pki\New-EwpLocalPki.ps1` and is set up by `ps\kafka\Setup-KafkaSecurity.ps1 -Phase Secure`.