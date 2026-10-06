# Notifications API

The **Notifications** bounded context (generic subdomain): it decides who is told what when the onboarding workflow moves, stores it, and pushes it live over **SignalR**. It has no MFE; the Shell shows the notifications in its profile area (bell, unread count, live toasts) and relays them to the MFE in its frame, so work queues reload on new work.

URL: `https://notifications-api.dev.localhost:46377` (launch profile `https`). Database: `EwpNotificationsDb` ([NotificationsDb/EwpNotificationsDb.sql](NotificationsDb/EwpNotificationsDb.sql)).

## Flow

```text
kyc.case.*, compliance.case.*, accounts.*  (Kafka)
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
| `ComplianceCaseCreated` | Compliance officers of the branch | "New compliance case …" |
| `AccountApplicationCreated` | Account officers of the branch | "New account application …" |
| `KycCaseApproved` / `Rejected` | The initiator (`user:{sub}`) | "etpar approved KYC for Camilla Parkers (APP-…001). Compliance review is next." |
| `ComplianceCaseApproved` / `Rejected` | The initiator | "olben cleared Camilla Parkers (APP-…001) for compliance (risk low)." |
| `AccountApplicationRejected`, `AccountOpened`, `AccountOpeningFailed` | The initiator | "Account 062-000 10000001 is open for Camilla Parkers (APP-…001). Onboarding is complete." |

The rules live in [Domain/NotificationRules.cs](Domain/NotificationRules.cs). People are named by LAN ID, customers by name; every notification also stores a **target** (e.g. `{"mfe":"kyc","page":"cases/view-details","recordId":3}`) that the Shell will open in a later increment (deep links).

## Endpoints

| Endpoint | Caller | Purpose |
|---|---|---|
| `GET v1/notifications?unreadOnly=` | the person (via the Shell BFF) | Newest 50 notifications addressed to them or to their role in their branch, with the unread count |
| `POST v1/notifications/{id}/read`, `POST v1/notifications/read-all` | the person | Mark read (per person; a branch-wide notification is read by each officer separately) |
| `/hubs/notifications` (SignalR) | the person (via the Shell BFF) | Live channel; the server sends `notification` messages |
| `POST internal/v1/notifications/events` | NotificationsSubscriber only (pinned M2M client) | Hand over one workflow event; idempotent per MessageId |

## Security

- **Audiences come from the token only.** The hub joins a connection to `user:{sub}` and to `staff:{role}:{branch}` for each work-queue role in the person's token; it exposes no method a client could use to join another group. The REST queries filter by the same audiences, and marking read checks that the notification is addressed to the caller.
- **No token in the browser, none in a URL.** The Shell BFF proxies REST and the hub and adds the access token as an `Authorization` header, also on the WebSocket upgrade. Because a browser cannot add Duende's anti-forgery header to a WebSocket, the Shell checks the `Origin` of hub requests instead (cross-site WebSocket hijacking).
- **A connection never outlives its token:** the hub closes it when the access token expires (`CloseOnAuthenticationExpiration`); the Shell reconnects through the BFF with a fresh token.
- **Stored first, pushed second.** A failed push loses nothing: the Shell loads unread notifications over REST when it starts or reconnects.
- Least-privilege database user `ewp_notifications_api`; problem details without internals.

## Scale-out

One instance here. With several instances, add a SignalR backplane (Redis, or Azure SignalR Service) so a push reaches the connections held by any instance. The subscriber already scales with its consumer group.