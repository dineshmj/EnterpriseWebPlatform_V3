# Operations Journey API

The operations administrator's Journey API (NestJS) - the customer's pattern, like the Audit Journey API:

```
Operations web (Next.js SPA + light BFF)  --token exchange-->  Operations Journey API  -->  every component's readiness probe
                                                                                         -->  the simulators' switch (loopback, Development only)
                                                                                         -->  the fault proxy's routes (Toxiproxy admin API, loopback; Development / Test / UAT)
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
| `GET /v1/journeys/operations/fault-routes` | `operations-journey.read` | `service.health.view` | Each API's route at the fault proxy: Open, Slow, Hanging or Cut, who changed it and when it opens again (Development / Test / UAT; 404 otherwise) |
| `PUT /v1/journeys/operations/fault-routes/{key}/state` | `operations-journey.write` | `simulator.behaviour.manage` + recent MFA when on | `{ "state": "Open" \| "Slow" \| "Hanging" \| "Cut" }` |

## The fault proxy (turning an API "off")

In Development every server-to-server call to an EWP API goes through one Toxiproxy process (`ps\toxiproxy\Start-Toxiproxy.ps1`, started on its own like Kafka and Temporal; routes in `ps\toxiproxy\toxiproxy.json`, ports 47101-47111): the BFFs, the Journey APIs, the Kafka subscribers, the Shell's notifications relay and the Partner Products Worker point at the route, not at the API (the .NET callers in `appsettings.Development.json`, the Node services in `runnow.bat`). Operations then makes a route:

| State | What the callers see |
|---|---|
| Open | Normal: the proxy passes the bytes on (TLS end to end - it never sees a token) |
| Slow | 5 seconds added to every answer |
| Hanging | The connection opens and the request still reaches the API, but its answer never comes back (the callers' timeouts decide; a change may have been made - like a lost answer) |
| Cut | Connection refused - the API is down for them |

The API itself keeps running, and Platform Health probes it directly, so it stays Healthy there with a "Cut at the proxy" mark. Not through the proxy: the browser to a BFF, the IDP and the Partner Bank's authorization server (token issuer and discovery), Kafka, this API (cutting it would take the controls away) and the Partner Bank API (its TLS name points at the Partner Bank's machine). Production, load and stress environments use the APIs' own addresses (`appsettings.json`) and deploy no proxy.

`FaultController` is the port and `ToxiproxyFaultController` its adapter (`src/faults/`); a test cluster would bind a service-mesh adapter instead (Istio / Linkerd fault injection) and nothing else would change. Settings: `OPERATIONS_FAULT_CONTROLS_ENABLED`, `OPERATIONS_FAULT_PROXY_ADMIN_URL` (`http://127.0.0.1:8474`), `OPERATIONS_FAULT_AUTO_REVERT_MINUTES` (15).

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Delegated tokens only: issuer, audience, `typ: at+jwt`, the endpoint's scope, and the Operations web BFF as the acting client | Any client, or a person's own token, could call the API | Token confusion; calls bypassing the web BFF | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 2 | Deny by default: an endpoint without an access rule refuses everybody; each needs a permission (`service.health.view`, `simulator.behaviour.manage`) | A new endpoint would be open to any signed-in person | Broken function-level authorization (OWASP API5) | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts), [platform.controller.ts](src/platform/platform.controller.ts) |
| 3 | Fail-closed configuration: `OPERATIONS_JOURNEY_ENVIRONMENT` (Development / Test / UAT / Production) has no default; outside Development the API refuses to start with MFA off, outside Development and Test with the simulator switch on, and outside Development, Test and UAT with the fault routes on; switches must read exactly `true` or `false`, audience and caller have no defaults | A forgotten or misspelt setting would silently switch MFA off, or leave the simulator switch on in production | Misconfiguration turning a control off (OWASP A05) | `loadOptions`, `assertSafeOutsideDevelopment` in [journey-options.ts](src/configuration/journey-options.ts); `SimulatorService` in [platform.services.ts](src/platform/platform.services.ts) |
| 4 | A simulator change needs a recent MFA sign-in (MFA is required outside Development) (the exchanged token keeps the person's sign-in time and methods) | A stolen session could break the platform for everybody | Account takeover leading to disruption | `recentMfa` in [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 5 | Every change logged with the person's LAN ID; a changed behaviour returns to Healthy, and a changed route opens again, by itself (15 minutes); every route is opened at start-up | Nobody would know who broke the demo; a forgotten "Down" would break the next one | Repudiation; lasting disruption | `SimulatorService.change` in [platform.services.ts](src/platform/platform.services.ts) |
| 6 | Only status and response time leave the API - never probe details or URLs | Internals (database, Kafka, backlog) would be exposed | Reconnaissance | `HealthProbeService` in [platform.services.ts](src/platform/platform.services.ts) |
| 7 | A JSON API for one server-side caller: `nosniff`, `default-src 'none'`, `frame-ancestors 'none'`, `no-store` | A browser could frame or cache its answers | Clickjacking; cached sensitive data | [main.ts](src/main.ts) |
| 8 | Request limits: overall (600 a minute, before any token check) and per person and calling client - 120 reads and 10 changes a minute; 429 with `Retry-After`; per instance (in memory; deployment-wide limits need a gateway or shared store, Threat-Model R-14), expired windows swept every minute | A stolen session or a runaway client could repeat privileged changes, or flood the API | Unrestricted resource consumption; abuse of privileged actions (OWASP API4) | [rate-limit.ts](src/http/rate-limit.ts), [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 9 | Errors as Problem Details (RFC 9457, `application/problem+json`); unexpected errors logged and answered with a generic detail | Stack traces or internal messages would reach the caller; each error would have its own shape | Information disclosure (CWE-209) | [problem-details.filter.ts](src/http/problem-details.filter.ts) |
| 10 | Parked messages of Accounts, Compliance and Partner Products: `GET v1/journeys/operations/parked-messages` (`outbox.view`) and `POST …/{context}/{messageId}/resend` (`workflow.reprocess`, step-up). For each call the operator's token is exchanged again (RFC 8693, this API's own exchange-only client) for that context's read or write scope; only the three known contexts and a GUID reach the path; downstream answers are translated | The Journey API could act as itself, reach a context it should not, or pass a Domain API's raw error to the screen | Broken function-level authorization (OWASP API5); information disclosure (CWE-209) | [parked-messages.controller.ts](src/recovery/parked-messages.controller.ts), [parked-messages.service.ts](src/recovery/parked-messages.service.ts), [token-exchange.service.ts](src/services/token-exchange.service.ts) |
| 11 | The fault proxy's admin API is unauthenticated, so it and every route listen on 127.0.0.1 only, the API accepts only a loopback `http` admin address, and only a known route and state reach it; a route is cut in the proxy, never in the API, and the proxy passes TLS through untouched | A setting or request could point the API at another machine's proxy or an arbitrary path; the proxy would see tokens | Server-side request forgery (CWE-918); token exposure | [journey-options.ts](src/configuration/journey-options.ts), [faults.controller.ts](src/faults/faults.controller.ts), [toxiproxy-fault-controller.ts](src/faults/toxiproxy-fault-controller.ts), `ps/toxiproxy/Start-Toxiproxy.ps1` |
| 12 | Container image: CI builds the API from the committed lockfile (`--frozen-lockfile`) - never a local build; production dependencies only; slim Node runtime without npm, npx, corepack or yarn, as the non-root user 1000 with its files read-only to it; scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); no secret inside - every setting comes from the environment, and `runnow.bat` and key material never enter the build | A developer-built image could carry anything; a package manager in a container helps an attacker who gets in; a secret in a layer stays in the registry for good | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 13 | TLS here or at the platform, never neither by accident: `OPERATIONS_JOURNEY_PLAIN_HTTP=true` (the image sets it) listens on plain HTTP 8080 - TLS between pods comes from the platform (service mesh mTLS); otherwise the development certificate (`OPERATIONS_JOURNEY_TLS_PFX_PATH`) is required, and both set refuse to start | A missing setting could quietly open plain HTTP | Misconfiguration (OWASP A05); cleartext transmission (CWE-319) | [listener-tls.ts](src/configuration/listener-tls.ts) |