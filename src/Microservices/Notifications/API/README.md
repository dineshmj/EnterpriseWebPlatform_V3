# Notifications API

The **Notifications** bounded context (generic subdomain): it decides who is told what when the onboarding workflow or a payment moves, stores it, and pushes it live over **SignalR**. It has no MFE; the Shell shows the notifications in its profile area (bell, unread count, live toasts) and relays them to the MFE in its frame, so work queues reload on new work.

URL: `https://notifications-api.dev.localhost:46377` (launch profile `https`). Database: `EwpNotificationsDb` ([NotificationsDb/EwpNotificationsDb.sql](NotificationsDb/EwpNotificationsDb.sql)).

## Flow

```text
kyc.case.*, compliance.case.screened / approved / rejected, accounts.*  (Kafka)
        │  NotificationsSubscriber (shared consume loop: retries, DLQ; M2M token)
        ▼
POST internal/v1/notifications/events      Inbox ─► NotificationRules ─► notifications rows   (one transaction)
        │                                                          │
        │                                           push to the audience's SignalR group (best effort)
        ▼
Shell BFF  ── proxies /bff/notifications and /hubs/notifications with the person's access token ──► browser
```

## Who is told what

| Event | Audience | Example |
|---|---|---|
| `KycCaseCreated` | KYC officers of the branch (`staff:kyc_officer:SYD001`) | "New KYC case — Jason Millers · APP-…002 is waiting for KYC review." |
| `ComplianceCaseScreened` | Compliance officers of the branch, when screening is done | "New compliance case — Camilla Parkers · APP-…001 was screened clear (risk low) and awaits a compliance decision." A screening alert says so, and notes when approval needs clearance level 5 |
| `AccountApplicationCreated` | Account officers of the branch | "New account application …" |
| `KycCaseApproved` / `Rejected` | The initiator (`user:{sub}`) | "etpar approved KYC for Camilla Parkers (APP-…001). Compliance review is next." |
| `ComplianceCaseApproved` / `Rejected` | The initiator | "olben cleared Camilla Parkers (APP-…001) for compliance (risk low)." |
| `AccountApplicationRejected`, `AccountOpened`, `AccountOpeningFailed` | The initiator | "Account 062-000 10000001 is open for Camilla Parkers (APP-…001). Onboarding is complete." |
| `PaymentApprovalRequired` | Payments officers of the branch (`staff:payments_officer:SYD001`) | "Payment awaiting approval — $4,750.00 to Jane Citizen (PAY-…) for CUST-100001 needs a payments officer's approval. The funds are reserved." |
| `PaymentCompleted` / `PaymentRejected` / `PaymentFailed` | The initiator | "Payment completed — $4,750.00 to Jane Citizen (PAY-…) was sent (approved by emcar)." |
| `PaymentCompensationFailed` | The initiator, the branch's payments officers, and **operations in every branch** (`staff:operations_administrator:*`) | "Payment needs attention — …"; operations: "Release to retry — … Open it and retry the release." |

The rules live in [Domain/NotificationRules.cs](Domain/NotificationRules.cs). People are named by LAN ID, customers by name; every notification also stores a **target**, the page a click opens: new work opens the record (e.g. `{"mfe":"kyc","path":"/v1/kyc/cases/view-details?caseId=3","recordId":3}`), progress opens the initiator's application list (`/v1/onboarding/applications/view-all`). It holds no server name: the Shell opens the path only through a microservice in the person's own menu, and that BFF checks it against its allow-list.

## Endpoints

| Endpoint | Caller | Purpose |
|---|---|---|
| `GET v1/notifications?unreadOnly=` | the person (via the Shell BFF) | Newest 50 notifications addressed to them or to their role in their branch, with the unread count |
| `POST v1/notifications/{id}/read`, `POST v1/notifications/read-all` | the person | Mark read (per person; a branch-wide notification is read by each officer separately) |
| `/hubs/notifications` (SignalR) | the person (via the Shell BFF) | Live channel; the server sends `notification` messages |
| `POST internal/v1/notifications/events` | NotificationsSubscriber only (pinned M2M client) | Hand over one workflow event; idempotent per MessageId |

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Strict access-token validation: issuer, audience, lifetime, signature against the IDP's keys, and only `typ: at+jwt` access tokens (RFC 9068); original claim names kept (`MapInboundClaims = false`) | A token issued for another API, an ID token or an expired token would be accepted | Token confusion and replay across APIs (OWASP API2 Broken Authentication) | `AddJwtBearer` in [Program.cs](Program.cs) |
| 2 | Deny by default: every endpoint needs an authorization policy | A new endpoint added without an attribute would be public | Broken function-level authorization (OWASP API5) | `RequireAuthorization` in [Program.cs](Program.cs) |
| 3 | Internal endpoints accept only the one machine client meant to call them (`client_id` pinned, not just the scope) | Any service holding the scope could post fake workflow steps (e.g. a forged "notify" message) | Service impersonation and spoofed workflow events | Policy `NotificationsSubscriberWrite` in [Program.cs](Program.cs); [InternalNotificationsController.cs](Controllers/InternalNotificationsController.cs) |
| 4 | Audiences come from the token only: the hub joins a connection to the person's own groups and offers no method to join another | A client could listen to other people's or other branches' notifications | Information disclosure across people and branches | [NotificationsHub.cs](Hubs/NotificationsHub.cs) |
| 5 | REST queries filter by the same audiences; marking read checks the addressee | Changing an ID could read or mark someone else's notification | BOLA / IDOR (OWASP API1) | [NotificationsController.cs](Controllers/NotificationsController.cs) |
| 6 | A hub connection is closed when its access token expires | A connection could outlive a revoked or expired session | Access after logout or deactivation | `CloseOnAuthenticationExpiration` in [Program.cs](Program.cs) |
| 7 | No token in the browser or in a URL: the Shell proxies REST and the hub and adds the token server-side | Tokens would sit in JavaScript or in query strings (and logs) | Token theft and leakage | [Shell Program.cs](../../../Shell/Program.cs) |
| 8 | Problem responses without internals (a `traceId` only; details are logged on the server) | Stack traces, SQL or exception messages would reach the caller | Information disclosure that helps an attacker (CWE-209, OWASP A05) | [ApiExceptionHandler.cs](Controllers/ApiExceptionHandler.cs) |
| 9 | Per-caller rate limits (person, machine client, IP), 429 with `Retry-After` | One caller, or a stolen token, could flood the API for everybody | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs), `AddEwpRateLimiting` in [Program.cs](Program.cs) |
| 10 | Own least-privilege database user `ewp_notifications_api`: its own database only, no DDL | A compromised API or leaked connection string would expose other contexts' data or let the schema be changed | Lateral movement and blast radius of a breach | [EwpServiceDbUsers.sql](../../../../db/EwpServiceDbUsers.sql) |
| 11 | HTTPS only (HSTS outside Development) and host filtering (`AllowedHosts`) | Tokens could travel over plain HTTP; a forged `Host` header could poison generated links | Interception (OWASP A02) and host-header attacks | `UseHsts` in [Program.cs](Program.cs); `AllowedHosts` in [appsettings.Development.json](appsettings.Development.json) |

## Scale-out

One instance here. With several instances, add a SignalR backplane (Redis, or Azure SignalR Service) so a push reaches the connections held by any instance. The subscriber already scales with its consumer group.