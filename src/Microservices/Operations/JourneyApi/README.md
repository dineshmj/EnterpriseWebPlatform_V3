# Operations Journey API

The operations administrator's Journey API (NestJS) - the customer's pattern, like the Audit Journey API:

```
Operations web (Next.js SPA + light BFF)  --token exchange-->  Operations Journey API  -->  every component's readiness probe
                                                                                         -->  the simulators' switch (loopback, Development only)
```

It holds no data. It needs no further token: the readiness probes are open (they report status only), and the simulators accept their switch from the same machine only.

| | |
|---|---|
| Address | https://operations-journey.dev.localhost:46382 |
| Start | `runnow.bat` (or `.\ps\run\Start-NodeServices.ps1`) |
| Accepts | Delegated tokens only: audience `operations-journey-api`, acting client `Operations.Microservice.Web.ClientID` |

## Endpoints

| Endpoint | Scope | Permission | What |
|---|---|---|---|
| `GET /v1/journeys/operations/health` | `operations-journey.read` | `service.health.view` | Every component's readiness: status (Healthy / Degraded / Unhealthy / Unreachable) and response time only |
| `GET /v1/journeys/operations/simulators` | `operations-journey.read` | `service.health.view` | The simulators' current behaviour (Development only; 404 otherwise) |
| `PUT /v1/journeys/operations/simulators/{key}/behaviour` | `operations-journey.write` | `simulator.behaviour.manage` + recent MFA when on | Healthy, Slow, Failing, Down or Refusing (Development only) |

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Delegated tokens only: issuer, audience, `typ: at+jwt`, the endpoint's scope, and the Operations web BFF as the acting client | Any client, or a person's own token, could call the API | Token confusion; calls bypassing the web BFF | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 2 | Deny by default: an endpoint without an access rule refuses everybody; each needs a permission (`service.health.view`, `simulator.behaviour.manage`) | A new endpoint would be open to any signed-in person | Broken function-level authorization (OWASP API5) | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts), [platform.controller.ts](src/platform/platform.controller.ts) |
| 3 | The simulator switch exists only where `OPERATIONS_SIMULATOR_CONTROLS_ENABLED=true` (Development / test); elsewhere it answers 404 - unset means off | An operator could switch off a dependency in production | Self-inflicted outage; misuse of a test feature | [journey-options.ts](src/configuration/journey-options.ts), `SimulatorService` in [platform.services.ts](src/platform/platform.services.ts) |
| 4 | A simulator change needs a recent MFA sign-in while MFA is on (the exchanged token keeps the person's sign-in time and methods) | A stolen session could break the platform for everybody | Account takeover leading to disruption | `recentMfa` in [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 5 | Every change logged with the person's LAN ID; a changed behaviour returns to Healthy by itself (15 minutes) | Nobody would know who broke the demo; a forgotten "Down" would break the next one | Repudiation; lasting disruption | `SimulatorService.change` in [platform.services.ts](src/platform/platform.services.ts) |
| 6 | Only status and response time leave the API - never probe details or URLs | Internals (database, Kafka, backlog) would be exposed | Reconnaissance | `HealthProbeService` in [platform.services.ts](src/platform/platform.services.ts) |
| 7 | A JSON API for one server-side caller: `nosniff`, `default-src 'none'`, `frame-ancestors 'none'`, `no-store` | A browser could frame or cache its answers | Clickjacking; cached sensitive data | [main.ts](src/main.ts) |