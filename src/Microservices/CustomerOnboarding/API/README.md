# Customer Onboarding API

Customers and their onboarding applications; starts the onboarding saga through its Outbox.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Strict access-token validation: issuer, audience, lifetime, signature against the IDP's keys, and only `typ: at+jwt` access tokens (RFC 9068); original claim names kept (`MapInboundClaims = false`) | A token issued for another API, an ID token or an expired token would be accepted | Token confusion and replay across APIs (OWASP API2 Broken Authentication) | `AddJwtBearer` in [Program.cs](Program.cs) |
| 2 | Deny by default, the same in every domain API: every controller needs an authenticated caller on top of its own policy, and any other endpoint without a policy needs one too (fallback policy); only health and metrics are explicitly anonymous | A new endpoint added without an attribute would be public | Broken function-level authorization (OWASP API5) | `MapEwpApiControllers`, `DenyAnonymousByDefault` in [Program.cs](Program.cs); [ApiEndpointAuthorization.cs](../../../Common/WebUtilities/Security/ApiEndpointAuthorization.cs) |
| 3 | Internal endpoints accept only the one machine client meant to call them (`client_id` pinned, not just the scope) | Any service holding the scope could post fake workflow steps (e.g. a forged "KYC approved" message) | Service impersonation and spoofed workflow events | Policy `OnboardingOutcomeSubscriberWrite` in [Program.cs](Program.cs); [InternalOnboardingApplicationsController.cs](API/Controllers/InternalOnboardingApplicationsController.cs) |
| 4 | Scope plus role per operation (`CustomerRead` / `CustomerWrite` / `OnboardingRead` / `OnboardingWrite`) | A read-only token could call write endpoints | Privilege escalation through a weaker token | [Program.cs](Program.cs) |
| 5 | Object-level checks: an agent sees only customers of their branch's city; lists filtered in the database | Changing an ID in a URL would open another branch's customer | BOLA / IDOR (OWASP API1) | [CustomerAccessScope.cs](Application/Abstractions/Authorization/CustomerAccessScope.cs), [CustomerResourceAuthorization.cs](Authorization/CustomerResourceAuthorization.cs) |
| 6 | Managing agent (relationship): only the customer's agent may update them or submit applications | Any agent of the branch could act on any customer | Unauthorised changes by colleagues (ReBAC) | [Customer.cs](Domain/Aggregates/Customer.cs) |
| 7 | The initiator recorded with the submission (Outbox) for separation of duties downstream | Nobody downstream could tell who started the application | Self-approval (maker = checker) | [OnboardingApplication.cs](Domain/Aggregates/OnboardingApplication.cs) |
| 8 | Problem responses without internals (a `traceId` only; details are logged on the server) | Stack traces, SQL or exception messages would reach the caller | Information disclosure that helps an attacker (CWE-209, OWASP A05) | [ApiExceptionHandler.cs](API/ErrorHandling/ApiExceptionHandler.cs) |
| 9 | Per-caller rate limits (person, machine client, IP), 429 with `Retry-After`; the subscribers' internal endpoints too (machine-client budget); counted per instance (R-14) | One caller, or a stolen token, could flood the API for everybody | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs), `AddEwpRateLimiting` in [Program.cs](Program.cs) |
| 10 | Own least-privilege database user `ewp_customer_onboarding_api`: its own database only, no DDL | A compromised API or leaked connection string would expose other contexts' data or let the schema be changed | Lateral movement and blast radius of a breach | [EwpServiceDbUsers.sql](../../../../db/EwpServiceDbUsers.sql) |
| 11 | HTTPS only (HSTS outside Development) and host filtering (`AllowedHosts`) | Tokens could travel over plain HTTP; a forged `Host` header could poison generated links | Interception (OWASP A02) and host-header attacks | `UseHsts` in [Program.cs](Program.cs); `AllowedHosts` in [appsettings.Development.json](appsettings.Development.json) |

## Monitoring

Readiness (`/health/ready`) turns **Degraded**, never Unhealthy, when an onboarding application has waited too long in one step ([OnboardingAgeHealthCheck.cs](Infrastructure/Monitoring/OnboardingAgeHealthCheck.cs)). The ages are required settings under `Onboarding:Alerts`:

| Step | Setting | Production | Development |
| --- | --- | --- | --- |
| Machine steps: Submitted, KYC completed, Compliance completed, compensating | `HandoverMinutes` | 15 | 5 |
| Officer steps: KYC in progress, Compliance in progress, account opening (the account officer's review, then core banking) | `OfficerDecisionMinutes` | 1440 (one day) | 60 |
| CompensationFailed | — | always | always |

The check reports counts per step only, because the endpoint is anonymous. It changes nothing: an application is never rejected or compensated because of its age. The Operations Platform Health screen shows the API as Degraded, and the `/metrics` health gauge carries the same state for alerting.