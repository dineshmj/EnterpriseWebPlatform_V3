# 07 — Light BFFs (Next.js SPA + server)

Deployables: `src/Microservices/Audit/BFF.Web`, `src/Microservices/Operations/BFF.Web` · Database: `EwpBffStateDb` schemas `audit_bff` / `operations_bff` (logins `ewp_audit_web` / `ewp_operations_web`)

## 1. Sign-in (OIDC client)

- [x] **A confidential client using Authorization Code + PKCE, with state and nonce.** Silent sign-in uses `prompt=none`. The Operations step-up route uses `prompt=login` with `acr_values=mfa`. → `lib/server/oidc.ts`, `app/api/auth/*/route.ts`
- [x] **The sign-in state lives in an encrypted `__Host-<App>-Web-Auth` cookie:** HttpOnly, Secure, SameSite=Lax, 10 minutes. A callback without it, or with one that cannot be decrypted, is refused (login CSRF). → `app/api/auth/callback/route.ts`
- [x] **Back-channel logout verifies the logout token** (issuer, audience, signature, the logout event, no nonce) and deletes the sessions of that `sid` / `sub`. → `app/api/auth/backchannel-logout/route.ts`
- [x] **Return URLs come from an allow-list** of this app's own pages. → `lib/server/return-url.ts`

## 2. Cookies and sessions

- [x] **The session cookie `__Host-<App>-Web-Session` is HttpOnly, Secure, SameSite=Lax and `Path=/`;** it is idle for at most 30 minutes. → `lib/server/config.ts`, `app/api/auth/callback/route.ts`
- [x] **Sessions are stored in PostgreSQL.** The key is the SHA-256 of the cookie value, and the tokens are encrypted with AES-256-GCM under a key that is not in the database. → `lib/server/session-store.ts`

## 3. Calls

- [x] **Data is loaded only by server actions,** so the browser never holds a token. Each call exchanges the person's token (RFC 8693) for a Journey API token naming this app as `act`. → `app/v1/<area>/actions.ts`
- [x] **State changes go only through server actions,** which Next.js accepts only as same-origin POSTs (its Origin / Host check).
- [x] **Problem Details from the Journey API** (`detail`, `code`, 429) are shown as messages, never as raw errors.

- [x] **Activity is reported to the Shell** (`BSS_MFE_ACTIVITY`, at most once a minute, the Shell's origin only, no data), so the Shell keeps its own session alive while the person works here. → `app/components/MfeShell.tsx`

## 4. Headers and configuration

- [x] **A Content-Security-Policy with a per-request nonce, against XSS:**
  - `script-src 'self' 'nonce-…' 'strict-dynamic'`;
  - nonce-only styles in production;
  - `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`.

  → `proxy.ts`
- [x] **`frame-ancestors` is the Shell and the IDP only,** against clickjacking. Both origins are required settings: with one missing, the request fails rather than falling back to a development origin. `nosniff` and `Referrer-Policy: strict-origin-when-cross-origin` are also sent.
- [x] **Fail-closed configuration:** a missing setting stops the app (`required()`). → `lib/server/config.ts`

## 5. Production (open)

- [ ] **Secret store.** The client secret and the session key come from a secret store; `runnow.bat` values are for Development only (R-10).