# Operations web (Next.js SPA + light BFF)

The operations administrator's front end, built in the customer's pattern - like the Audit web: one Next.js application that is both the SPA and a light BFF, running as a Node server.

```
Operations SPA (browser, in the Shell's frame)
--server action--> this app's server side (session, tokens)
--token exchange (act = this app)--> Operations Journey API (NestJS)
--> every component's readiness probe, and (Development only) the simulators' switch
```

URL: `https://operations.dev.localhost:46381`. Runs outside Visual Studio: `runnow.bat`, or `.\ps\run\Start-NodeServices.ps1`. Menu: **Operations → Platform → Platform Health** and **Simulator & Fault Controls** (operations administrators only - `daniel.ops`).

## The pages

| Page | What |
|---|---|
| `/v1/operations/platform/view-all` | **Platform Health**, read-only for the platform's behaviour, every environment. **Components**: every deployable part's readiness (Healthy / Degraded / Unhealthy / Unreachable) and response time, grouped, refreshed every 10 seconds. **Simulated external systems** (Development and test only): what each simulator is doing now, when it goes back to Healthy and who changed it - no switch. **Parked messages**: the resend (recovery work, needed in production too). |
| `/v1/operations/platform/fault-controls` | **Simulator & Fault Controls**. **Platform APIs (fault proxy)** (Development, test and UAT): each of the platform's own APIs, its server-side callers, and Open / Slow / Hanging / Cut - an API "off" for its callers while it keeps running (the Operations Journey API's README explains the proxy). **Simulated external systems** (Development and test): the Screening Provider, Core Banking and Payment Network simulators - Healthy, Slow, Failing, Down, Refusing, and LosingAnswers for the Payment Network. Where a control does not exist the Journey API answers 404 and the page leaves that card out (both gone: a notice). Platform Health marks an API whose route is not Open ("Cut at the proxy"). |

## How it works

- **Server actions** (`app/v1/operations/actions.ts`) are the browser's ajax calls: each reads the session, refreshes the person's token if needed, exchanges it for a short-lived token for the Journey API - with the narrowest scope: `operations-journey.read`, or `operations-journey.write` for a simulator change - and calls it. The browser never holds a token.
- **Sessions** in PostgreSQL (`EwpBffStateDb`, schema `operations_bff`, user `ewp_operations_web`): the SHA-256 of the session cookie (never the cookie) and the tokens encrypted with AES-256-GCM (`OPERATIONS_WEB_SESSION_KEY`, not in the database). 30 minutes sliding.
- **Sign-in** like every MFE: silently from the Shell's frame (authorization code + PKCE), front- and back-channel logout.

## First run

1. The development certificate exported once (see the Audit Journey API's README).
2. Hosts file: `127.0.0.1    operations.dev.localhost` and `127.0.0.1    operations-journey.dev.localhost`.
3. `EwpBffStateDb` and the database users recreated (the `operations_bff` schema and `ewp_operations_web`).
4. Start the Journey API first, then this app (`Start-NodeServices.ps1` does both, in order).

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens only on the server; the browser calls server actions, which Next.js accepts only from this app's own origin | Any XSS bug could read the tokens; another site could invoke the actions | Token theft; cross-site action calls | [actions.ts](app/v1/operations/actions.ts) |
| 2 | Each call exchanges the person's token for one aimed at the Journey API, with only the scope it needs (read, or write for a simulator change) | The Journey API could not tell which application calls for the person; a read would carry write rights | Confused deputy; excessive privilege | `exchangeForJourneyApi` in [oidc.ts](lib/server/oidc.ts) |
| 3 | Sessions in PostgreSQL keyed by the cookie's SHA-256, tokens AES-256-GCM encrypted with a key outside the database | A database reader could hijack or read a session | Session hijacking; token theft from storage | [session-store.ts](lib/server/session-store.ts) |
| 4 | Cookies `__Host-`, HttpOnly, Secure, SameSite | Script-readable or cross-site cookies | Session theft, CSRF | [config.ts](lib/server/config.ts) |
| 5 | CSP with a per-request nonce; framed only by the Shell (`frame-ancestors`; the Shell and IDP origins are required settings, no development fallback); the Shell protocol only with the Shell's static origin | Injected script would run; any site could frame the screen | XSS; clickjacking; message spoofing | [proxy.ts](proxy.ts), [MfeShell.tsx](app/components/MfeShell.tsx) |
| 6 | Sign-in returns only to one of this app's two pages (exact match), else to Platform Health | A crafted link could bounce the person to a phishing site | Open redirect (CWE-601) | [return-url.ts](lib/server/return-url.ts) |
| 7 | Front- and back-channel logout end the session | A session would survive signing out of the platform | Session reuse after logout | `app/api/auth/*-logout` |
| 8 | The switches are on their own page, never on Platform Health; they work only when the Journey API has them enabled (Development / test; otherwise 404 and the page says so), and every change is decided and recorded there | A production screen could switch off a dependency; a dashboard left open could change behaviour by a stray click | Self-inflicted outage | [fault-controls/page.tsx](app/v1/operations/platform/fault-controls/page.tsx), [view-all/page.tsx](app/v1/operations/platform/view-all/page.tsx), the Journey API's README |
| 9 | Step-up for risky actions: `GET /api/auth/step-up` asks the IDP (`prompt=login`, `acr_values=mfa`) for a fresh authenticator code and renews this session's tokens; when the API answers 403 `mfa_required` the screen asks the Shell, which shows it in an in-page overlay (`?in=shell`: it ends on the Shell's own fixed done page); outside the Shell a small window ends on `/api/auth/step-up-done` - fixed addresses, never taken from the request - and the change is retried once | The person would have to sign out and in again; a redirect target taken from the request could be abused | Friction that pushes people to weaken MFA; open redirect | [step-up/route.ts](app/api/auth/step-up/route.ts), [oidc.ts](lib/server/oidc.ts); [step-up.ts](lib/step-up.ts), the simulator change in [fault-controls/page.tsx](app/v1/operations/platform/fault-controls/page.tsx) |
| 10 | Parked messages card on Platform Health: resend needs a reason of 10-500 characters, checked here and again downstream; a 403 `mfa_required` opens the step-up window and retries once | A resend without a recorded reason, or a resend loop | Repudiation; OWASP A04 (insecure design) | [ParkedMessagesCard.tsx](app/v1/operations/platform/view-all/ParkedMessagesCard.tsx), [actions.ts](app/v1/operations/actions.ts) |
| 11 | Shell–MFE messages go to, and are accepted from, the Shell's origin only - the Shell's origin is this BFF's own setting, read at run time (`/ewp-runtime-config.js`, [shell-origin.ts](lib/shell-origin.ts)) - never built into the front end; missing or not https = no message is sent or accepted | A hostile page that frames the MFE could pose as the Shell; a Shell origin built into the image would tie it to one environment | Cross-origin message spoofing (CWE-346) | [MfeShell.tsx](app/components/MfeShell.tsx), [route.ts](app/ewp-runtime-config.js/route.ts), [layout.tsx](app/layout.tsx) |
| 12 | Container image: CI builds the app from the committed lockfile (`--frozen-lockfile`) - never a local build; production dependencies only; slim Node runtime without npm, npx, corepack or yarn, as the non-root user 1000 with its files read-only to it; scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); no secret inside - every setting comes from the environment, and `runnow.bat` and key material never enter the build | A developer-built image could carry anything; a package manager in a container helps an attacker who gets in; a secret in a layer stays in the registry for good | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 13 | TLS here or at the platform, never neither by accident: `OPERATIONS_WEB_PLAIN_HTTP=true` (the image sets it) listens on plain HTTP 8080 - TLS ends at the ingress; otherwise the development certificate (`OPERATIONS_WEB_TLS_PFX_PATH`) is required, and both set refuse to start | A missing setting could quietly open plain HTTP | Misconfiguration (OWASP A05); cleartext transmission (CWE-319) | [server.mjs](server.mjs) |
| 14 | Forwarded headers only from the ingress: `X-Forwarded-For` / `-Proto` are kept only from the addresses in `OPERATIONS_WEB_TRUSTED_PROXIES` (IPs or CIDRs), cut to one hop; `X-Forwarded-Host` / `-Port` are always dropped (Next.js checks a server action's Origin against the Host the ingress passes on). `/health/live` and `/health/ready` are answered before Next.js: anonymous, nothing internal | Any client could claim another address, scheme or host | Spoofed forwarded headers (CWE-348); misconfiguration (OWASP A05) | [forwarded-headers.mjs](forwarded-headers.mjs), [server.mjs](server.mjs) |