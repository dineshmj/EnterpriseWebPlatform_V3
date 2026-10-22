# BSS Shell — Requirements

**Component:** EnterpriseWebPlatform.BSS.BFFWeb (Shell BFF + Shell SPA)  
**Status:** Present

---

## 1. Purpose and Boundary

The Shell is the **composition host** of the Banking Services System. It provides branding, sign-in, navigation, the Application Workspace and the notification display (bell, unread count, live toasts), and it hosts each bounded context's MFE in an iframe.

The Shell is **deliberately business-neutral**:

| The Shell does | The Shell never does |
|---|---|
| Authenticate the user and hold the Shell session | Call business APIs or read business databases |
| Render the role-aware menu from its own Menu DB | Resolve, interpret or validate business identifiers |
| Host MFEs and relay opaque workspace context between them | Implement any business rule, or coordinate a saga |
| Display notifications that are already addressed and authorized | Decide who should receive a business notification |
| Own the user-facing logout | Treat menu visibility as authorization |

### Deployable components

| Component | Location | Technology |
|---|---|---|
| Shell SPA | `client-app` | Next.js static export, served by the Shell BFF |
| Shell BFF | `src/Shell` | ASP.NET Core 10 + Duende BFF |
| Menu database | `EwpBssShellDb` (`MenuDB/EwpBssShellDb.sql`) | PostgreSQL |

---

## 2. Navigation Menu

- The menu metadata is owned by the Shell: `microservices` (name and MFE base URL), `management_areas`, `menu_items` (title, route, icon), and `menu_items_and_roles` (which role codes see an item).
- MFE base URLs come from the Menu DB, never from Shell code. Adding a bounded context adds menu rows, not Shell code.
- The Shell shows only the items mapped to the user's role claims. **This is a convenience, not security.** Every MFE, BFF and API authorizes independently.
- Role codes in `menu_items_and_roles` must match the IDP role codes exactly.
- The menu must not promise more than the APIs allow: an item lists exactly the roles its API admits, so no item ends in "You are not permitted …". Today: Customer Onboarding - agents, plus operations and platform administrators (read, all branches); KYC, Compliance and Accounts - their own officers only; Payments - agents and payments officers, plus operations (read, all branches); Partner Products (Digital Gold) - agents; Operations - operations administrators; Audit - auditors, who see every context's activity there only (every look recorded), never through the operational screens.

### Welcome screen

Until the person opens a page, the Shell shows a welcome screen instead of an empty frame; a **Home** button in the workspace bar returns to it (after the MFE in the frame agrees, as for any navigation). It stays business-neutral and calls no business API:

- **Who you are**, from the token: name, roles, branch and LAN ID (the Shell requests the `organization` scope for display only).
- **Unread notifications** (latest three), each with **Open ›** when it can be opened (§7).
- **Resume where you left off**: the last page opened from the menu, kept in this browser per user (`localStorage`). Shown only if that page is still in the person's menu; if storage is unavailable, the tile is simply absent.
- **Your workspaces**: one card per microservice in the person's menu, listing its pages. Every link uses the menu's navigation.

Deliberately absent: opening a page automatically, and business figures such as queue sizes (those would need business APIs; a dashboard belongs in an MFE).

---

## 3. Application Workspace

The Workspace shows contextual information about the user's current work (for example, the selected customer) while the user moves between MFEs. It is separate from the navigation menu.

```text
persistentContext  — root context that stays visible across MFEs (e.g. the selected Customer)
currentContext     — information relevant to the MFE / page currently displayed
retainedContext    — earlier context an MFE may reuse; not rendered by the Shell
```

Each item is a `{ title, value }` pair. The context is **opaque** to the Shell: it stores, renders and hands it over, but only the MFE that owns the business meaning creates, updates or discards it. Workspace context is a human-context aid, never authoritative data. A receiving MFE re-reads anything it needs from its own BFF.

**Picking a record.** Whenever the user picks a record, the MFE publishes it (`publishSelection` in each MFE's `MfeShell`):

- `root` (the customer) becomes `persistentContext`, and the picked record becomes `currentContext`.
- If the root is the customer already shown (the titles the two have in common carry the same values), the existing root is kept and enriched, and `retainedContext` survives.
- A different customer replaces the root and discards all subordinate context.

| MFE | Picked record | Root | Current |
|---|---|---|---|
| Customer Onboarding | Customer directory → Start onboarding | Customer ID, Customer Number | — |
| | Application submitted, or picked in "Recent applications" | Customer ID, Customer Number | Application ID, Application Number, Status |
| Customer KYC | Case details opened | Customer Number | Application Number, KYC Case, KYC Status |
| | Case picked for review | Customer Number | Application Number, KYC Case, Reviewing (stage), Stage Status |
| | Stage decision recorded | Customer Number | Application Number, KYC Case, the decided stage's status, KYC Status |

**Reusing context.** A KYC review page opens on the case whose `Application Number` is in the handed-over current or retained context, when that case is in the queue. An explicit `?caseId=` link takes precedence.

---

## 4. Shell ↔ MFE Protocol (`postMessage`)

| Message | Direction | Purpose |
|---|---|---|
| `BSS_MFE_READY` | MFE → Shell | The MFE has loaded and can receive context |
| `BSS_CONTEXT_HANDOFF` | Shell → MFE | The Shell hands over the current workspace context |
| `BSS_CONTEXT_UPDATE` | MFE → Shell | The MFE replaces the workspace context |
| `BSS_NAVIGATION_REQUEST` | Shell → MFE | The Shell asks whether it may navigate away (with a `requestId`) |
| `BSS_NAVIGATION_RESPONSE` | MFE → Shell | `allowed: true / false`. A dirty form asks the user first. |
| `BSS_MFE_ACTIVITY` | MFE → Shell | The person is active in the MFE (a click or key; at most once a minute; no data). The Shell renews its own session, at most every 5 minutes. |
| `BSS_STEP_UP_REQUEST` | MFE → Shell | An API answered 403 `mfa_required`: the MFE asks for a fresh authenticator code (a `requestId` only). The Shell shows its in-page overlay framing that MFE's own `/api/auth/step-up?in=shell` (built from the origin it accepted, never from the message). |
| `BSS_STEP_UP_RESULT` | Shell → MFE | `confirmed: true / false` for that `requestId`: the code was confirmed (the step-up ended on the Shell's own `/step-up-done.html`), or the person cancelled (Cancel, Escape, 5 minutes). The MFE retries the action once; the API checks the fresh MFA itself. |

Rules:

1. Both sides validate **`event.origin` and `event.source`** before acting, and always post to an explicit target origin, never `'*'`.
2. The Shell accepts messages only from the iframe it created.
3. An MFE determines its trusted parent from a **static allow-list** of Shell origins, not from `document.referrer`.
4. If an MFE does not answer a navigation request within the timeout, navigation proceeds (fail-open by design, because only unsaved form data is at risk).

---

## 5. MFE Sign-in

When the user selects a menu item, the iframe is pointed at the MFE BFF's `/api/auth/silent-login?returnUrl=<route>`. The MFE BFF validates `returnUrl` against an allow-list and signs in silently against the IDP SSO session (`prompt=none`). Each MFE BFF keeps its own session cookie on its own host name.

---

## 6. Logout

The Shell owns the user-facing logout:

1. End the Shell BFF session.
2. Start OIDC end-session at the IDP (with `id_token_hint`, so no prompt is needed).
3. The IDP ends the SSO session and renders the front-channel logout iframes, which call each participating MFE BFF's `/signout-oidc`, ending its local session.

The Shell's additional best-effort call to `POST /api/auth/silent-logout` on each MFE BFF is not implemented by the BFFs and is superseded by front-channel logout; it can be removed from the Shell.

Cookies stay isolated per host name (`*.dev.localhost` locally), and a logout must leave no MFE session usable.

---

## 7. Workflow Notifications

- A separate **Notifications** context (the [NotificationsSubscriber](../../src/AsyncWorkflows/Subscribers/Notifications/NotificationsSubscriber/README.md) plus the [Notifications API](../../src/Microservices/Notifications/API/README.md) with its SignalR hub, outside the Shell) maps business events to **neutral notifications**: audience, title, text and a stored target (the path a click opens). It applies the notification-audience policy of [Authorization-Model §11](../Authorization-Model.md#11-notification-authorization).
- **Present (4a):** the Shell BFF proxies `/bff/notifications` (REST: list, mark read) and `/hubs/notifications` (SignalR) to the Notifications API with the person's access token. It holds no notification logic and no Kafka connection. The hub route skips Duende's anti-forgery header (a browser cannot send it on a WebSocket) and checks the request `Origin` instead.
- **Present (4b):** the bell with the unread count, a panel with the latest 50 (mark one or all read) and live toasts in the profile area; the workspace bar shows whether live updates are connected. The Shell holds the **only** live connection per browser (`@microsoft/signalr`, reconnecting with back-off while the Shell's session lasts; unread notifications are reloaded after every reconnect; once the session has ended the workspace bar shows **Session ended** with **Sign in again** instead of reconnecting for ever) and relays each live notification to the MFE in the frame as `BSS_NOTIFICATION` (to that MFE's origin only). The MFE decides what to do: the KYC, Compliance and Accounts work queues reload on new work for them, and the CO application list reloads on progress.
- **Present (4c): deep links.** A notification stores a path (`/v1/kyc/cases/view-details?caseId=3`; progress opens the initiator's applications list at that application, `/v1/onboarding/applications/view-all?applicationNumber=APP-20261012-000001`). The notification never names a server: the Shell checks the path's format and finds the server in the person's **own menu**, as the microservice with a page in the same area (`/v1/kyc`); if none, the notification is shown but opens nothing. A click goes through the same navigation as a menu click: the current MFE is asked first and may keep the person on a screen with unsaved changes; then silent sign-in, where the target BFF checks the path against its allow-list (a page may carry exactly one parameter: a numeric id, or an upper-case business reference such as an application number - each BFF accepts only its own exact shapes), and the MFE and API authorize the record again.
- The Shell opens an authenticated connection for the signed-in user and **only renders** what it receives. It never inspects business payloads and never broadcasts to all users.
- A deep link uses the normal menu navigation and MFE sign-in, so the target MFE authorizes again.

---

## 8. Security Requirements and Gaps

| Requirement | Status |
|---|---|
| Tokens never reach browser JavaScript (BFF pattern) | Present |
| `postMessage` origin and source validation in the Shell | Present |
| MFEs use a static parent-origin allow-list | Present (each BFF's `ShellOrigin` setting, served at run time as `/ewp-runtime-config.js` and read by the MFE's `shell-origin.ts`; missing = no messages; no `document.referrer`, no `'*'`) |
| Step-up without a pop-up | Present: the in-page overlay (`BSS_STEP_UP_REQUEST` / `_RESULT`); only for the MFE in the frame; it frames the MFE's own fixed step-up address; the IDP lets the Shell frame its step-up code page only; "Nothing shown? Use a separate window" as the fallback |
| CSP `frame-ancestors` on the Shell and every MFE | Present: the Shell cannot be framed (`frame-ancestors 'none'`, `X-Frame-Options: DENY`) - except its static `/step-up-done.html`, which only the Shell itself may frame (`'self'`); MFE BFFs may be framed only by the Shell (and by the IDP, for front-channel logout); `nosniff` and a referrer policy everywhere |
| Shell session lifetime aligned with the MFE BFFs | Present (30 minutes, sliding) |
| Middleware order `UseBff()` before `UseAuthorization()` | Present |
| Server-side sessions (tokens not carried in the cookie) | Present in the Shell, every .NET MFE BFF and the Audit web app (Next.js; its own session table in `EwpBffStateDb`, schema `audit_bff`, tokens encrypted): Duende sessions and the Data Protection keys (which encrypt the cookies) are stored in PostgreSQL `EwpBffStateDb`, one schema and database user per BFF; a restart or a second instance keeps everybody signed in, and expired sessions are cleaned up. The key ring is encrypted at rest (certificate in `DataProtection:CertificatePath`; DPAPI only in Development on Windows); without either the BFF refuses to start. The NestJS KYC BFF stores its `express-session` sessions there too (schema `kyc_bff`, user `ewp_kyc_bff`; hashed session key, session encrypted with AES-256-GCM). |
| Logout propagation to every MFE BFF | Present via IDP front-channel logout (each BFF's `/signout-oidc`) |
| Rate limiting (OWASP API4) | Present: the Shell BFF and every .NET MFE BFF and API limit requests per signed-in person (600/min, of which 60 changes), per machine client (3,000/min) and per IP before sign-in (120/min); over the limit: 429 with Retry-After, shown as a message. Static files, health probes, hubs and internal endpoints are not limited |
| An MFE's API call with an expired or lost session | Present: the .NET MFE BFFs answer **401** on `/bff/api` (never a redirect the browser cannot follow); the MFE signs in again silently and returns to the same page. Only `silent-login` and `user` are anonymous; `csrf` and `logout` require a session |
| Notification display | Present: bell, toasts, mark read; relayed to the MFE in the frame; a click opens the record (4c) |