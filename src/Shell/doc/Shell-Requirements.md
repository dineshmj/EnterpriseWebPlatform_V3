# BSS Shell — Requirements

**Component:** EnterpriseWebPlatform.BSS.BFFWeb (Shell BFF + Shell SPA)  
**Status:** Present

---

## 1. Purpose and Boundary

The Shell is the **composition host** of the Banking Services System. It provides branding, sign-in, navigation, the Application Workspace and (planned) the notification display, and it hosts each bounded context's MFE in an iframe.

The Shell is **deliberately business-neutral**:

| The Shell does | The Shell never does |
|---|---|
| Authenticate the user and hold the Shell session | Call business APIs or read business databases |
| Render the role-aware menu from its own Menu DB | Resolve, interpret or validate business identifiers |
| Host MFEs and relay opaque workspace context between them | Implement any business rule, or coordinate a saga |
| Display notifications that are already addressed and authorized (planned) | Decide who should receive a business notification |
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

---

## 3. Application Workspace

The Workspace shows contextual information about the user's current work (for example, the selected customer) while the user moves between MFEs. It is separate from the navigation menu.

```text
persistentContext  — root context that stays visible across MFEs (e.g. the selected Customer)
currentContext     — information relevant to the MFE / page currently displayed
retainedContext    — earlier context an MFE may reuse; not rendered by the Shell
```

Each item is a `{ title, value }` pair. The context is **opaque** to the Shell: it stores, renders and hands it over, but only the MFE that owns the business meaning creates, updates or discards it. Workspace context is a human-context aid, never authoritative data. A receiving MFE re-reads anything it needs from its own BFF.

---

## 4. Shell ↔ MFE Protocol (`postMessage`)

| Message | Direction | Purpose |
|---|---|---|
| `BSS_MFE_READY` | MFE → Shell | The MFE has loaded and can receive context |
| `BSS_CONTEXT_HANDOFF` | Shell → MFE | The Shell hands over the current workspace context |
| `BSS_CONTEXT_UPDATE` | MFE → Shell | The MFE replaces the workspace context |
| `BSS_NAVIGATION_REQUEST` | Shell → MFE | The Shell asks whether it may navigate away (with a `requestId`) |
| `BSS_NAVIGATION_RESPONSE` | MFE → Shell | `allowed: true / false`. A dirty form asks the user first. |

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

## 7. Workflow Notifications (planned)

- A separate **Notifications** component (a Kafka subscriber plus a SignalR hub, outside the Shell) maps business events to **neutral notifications**: recipient subject ID, title, text and an optional deep link (menu route plus opaque context). It applies the notification-audience policy of [Authorization-Model §11](../../../doc/Authorization-Model.md#11-notification-authorization).
- The Shell opens an authenticated connection for the signed-in user and **only renders** what it receives. It never inspects business payloads and never broadcasts to all users.
- A deep link uses the normal menu navigation and MFE sign-in, so the target MFE authorizes again.

---

## 8. Security Requirements and Gaps

| Requirement | Status |
|---|---|
| Tokens never reach browser JavaScript (BFF pattern) | Present |
| `postMessage` origin and source validation in the Shell | Present |
| MFEs use a static parent-origin allow-list | Present (`NEXT_PUBLIC_SHELL_ORIGIN`, built into each MFE; no `document.referrer`, no `'*'`) |
| CSP `frame-ancestors` on the Shell and every MFE | Present: the Shell cannot be framed (`frame-ancestors 'none'`, `X-Frame-Options: DENY`); MFE BFFs may be framed only by the Shell (and by the IDP, for front-channel logout); `nosniff` and a referrer policy everywhere |
| Shell session lifetime aligned with the MFE BFFs | Present (30 minutes, sliding) |
| Middleware order `UseBff()` before `UseAuthorization()` | Present |
| Server-side sessions (tokens not carried in the cookie) | Present in the Shell and CO BFF (Duende in-memory store; a persistent store is needed for multiple instances). The NestJS KYC BFF uses the in-memory `express-session` store. |
| Logout propagation to every MFE BFF | Present via IDP front-channel logout (each BFF's `/signout-oidc`) |
| Notification display | Planned |
