# AsyncWorkflows.Infrastructure.Subscribers

The shared subscriber pipeline every worker runs on.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Offsets committed only after processing; permanent failures dead-lettered with the reason, transient ones retried | A crash could lose a message, and a poison message could block the partition | Lost events; denial of service by one bad message | [KafkaSubscriberHostedService.cs](KafkaSubscriberHostedService.cs), [MessageProcessing.cs](MessageProcessing.cs) |
| 2 | Required settings and the client secret validated at start-up (`ValidateOnStart`), no defaults | A worker could run with an empty secret or no dead-letter topic | Misconfiguration becoming a silent gap | [SubscriberServiceCollectionExtensions.cs](SubscriberServiceCollectionExtensions.cs) |
| 3 | Machine token cached and renewed shortly before expiry | Every message would request a token from the IDP | Self-inflicted denial of service on the IDP | [CachedM2MTokenClient.cs](CachedM2MTokenClient.cs) |
| 4 | Liveness and readiness with no internals in the response | A stuck worker would keep its partitions; a probe response could leak details | Silent outage; information disclosure | [SubscriberHealth.cs](SubscriberHealth.cs) |