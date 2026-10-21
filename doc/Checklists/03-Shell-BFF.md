# 03 — Shell BFF

Deployable: `src/Shell` (ASP.NET Core + Duende BFF) · Databases: `EwpBssShellDb` (menu), `EwpBffStateDb` schema `shell_bff`

## 1. Sign-in (OIDC client)

- [x] **It is a confidential client using Authorization Code + PKCE.** The code is exchanged on the server, with the client secret. → `src/Shell/Program.cs` (`ResponseType = "code"`)
- [x] **Its scopes:** `openid profile email roles organization offline_access`, plus `notifications.read` and `notifications.write`, and nothing else. *Because* the Shell is business-neutral; it calls only the Notifications API.
- [x] **Tokens are kept in the server-side session** (`SaveTokens`) and never sent to the browser.
- [x] **Claim names are kept as issued** (`MapInboundClaims = false`, `DefaultMapInboundClaims = false`).

## 2. Cookies

- [x] **The session cookie `__Host-BSS-Shell-bff` is HttpOnly, Secure, SameSite=Lax and has `Path=/`.** *Because:*
  - HttpOnly: script cannot read it;
  - Secure: it never travels over HTTP;
  - SameSite=Lax: it is not sent on cross-site sub-requests;
  - `__Host-`: it is locked to this host.

  → `Program.cs` (the cookie name is set there)
- [x] **The session lasts 30 minutes and slides** (instead of the framework's 14 days). It slides on real activity only: the SPA calls `/bff/user` when the person is active (in the Shell or in the hosted application), and its status checks use `/bff/user?slide=false`.
- [x] **Only the correlation and nonce cookies use SameSite=None (with Secure),** and only for the sign-in round trip. *Because* they must survive the cross-site return from the IDP.
- [x] **The ASP.NET session cookie is HttpOnly.**

## 3. Sessions and keys

- [x] **Sessions are server-side** in PostgreSQL (`AddEntityFrameworkServerSideSessions`); the cookie is only a reference. *Because* back-channel logout can then end the session, and a restart signs nobody out.
- [x] **Data Protection keys are in `EwpBffStateDb` (schema `shell_bff`), with the fixed application name `ewp-shell-bff`.** *Because* every instance can read every cookie, and no other BFF can. → `src/Common/WebUtilities/Security/PersistentDataProtection.cs`
- [x] **Front-channel and back-channel logout end this BFF's session.**

## 4. Requests

- [x] **Duende's anti-forgery header (`X-CSRF: 1`) is required on BFF and API calls.** `UseBff()` runs after authentication and before authorization.
- [x] **WebSocket requests to the notifications hub need a same-origin `Origin` header.** *Because* a browser cannot add the anti-forgery header to a WebSocket upgrade, so the Origin check is what stops cross-site WebSocket hijacking.
- [x] **The BFF proxies only to the Notifications API** (`/bff/notifications`, `/hubs/notifications`), with the person's token. → `Program.cs` (`MapRemoteBffApiEndpoint`)
- [x] **The menu is filtered on the server by the person's roles** (`[Authorize]`). → `Controllers/MenuController.cs`

## 5. Headers

- [x] **A Content-Security-Policy on every response, against XSS:**
  - `default-src 'self'`;
  - scripts and styles only from self or by SHA-256 hash, never `'unsafe-inline'`;
  - `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`, `connect-src 'self'`.

  → `ContentSecurityPolicy.cs`
- [x] **`frame-src` is an allow-list:** the MFE origins, the IDP and `'self'` (the step-up done page) only. `frame-ancestors 'none'` and `X-Frame-Options: DENY` mean the Shell is never framed (against clickjacking) - except its static `/step-up-done.html` (no script, no data), which only the Shell itself may frame (`'self'`, `SAMEORIGIN`). → `Program.cs`
- [x] **Also sent:** `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`.
- [x] **HTTPS only** (HSTS outside Development), and `AllowedHosts` set.

## 6. Production (open)

- [ ] **Secret store.** The client secret and the database password come from a secret store (R-10).
- [ ] **Data Protection keys.** Their certificate moves to a key vault, with rotation.