# Common.Observability

Logs, metrics, traces and health endpoints, set up once for every .NET component.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Request logs carry the path without the query string, the status and the caller's subject or client ID; never bodies, headers, names or tokens | Tokens, personal data or search terms could end up in the log store | Sensitive-data exposure through logs (OWASP A09) | [EwpLogging.cs](EwpLogging.cs) |
| 2 | Health responses list each check's status and description only | Exception details or connection strings could be served to anyone | Information disclosure | [HealthEndpoints.cs](HealthEndpoints.cs) |
| 3 | Every request and authorization decision measured (`/metrics`), including rate-limited requests | An attack (floods, repeated refusals) would go unnoticed | Undetected abuse (OWASP A09 Logging and Monitoring Failures) | [MetricsEndpoints.cs](MetricsEndpoints.cs), [ObservabilityExtensions.cs](ObservabilityExtensions.cs) |

**Not yet:** `/metrics` is anonymous, like the health endpoints: in a deployment it must be reachable only from the cluster network (Prometheus), not from the internet.