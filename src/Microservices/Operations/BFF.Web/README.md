# Operations web (Next.js SPA + light BFF)

The operations administrator's front end, built in the customer's pattern - like the Audit web: one Next.js application that is both the SPA and a light BFF, running as a Node server.

```
Operations SPA (browser, in the Shell's frame)
--server action--> this app's server side (session, tokens)
--token exchange (act = this app)--> Operations Journey API (NestJS)
--> every component's readiness probe, and (Development only) the simulators' switch
```

URL: `https://operations.dev.localhost:46381`. Runs outside Visual Studio: `runnow.bat`, or `.\ps\run\Start-NodeServices.ps1`. Menu: **Operations → Platform → Platform Health** (operations administrators only - `daniel.ops`).

## The page

| Page | What |
|---|---|
| `/v1/operations/platform/view-all` | **Components**: every deployable part's readiness (Healthy / Degraded / Unhealthy / Unreachable) and response time, grouped, refreshed every 10 seconds. **Simulated external systems** (Development and test only): the Screening Provider, Core Banking and Payment Network simulators' behaviour - Healthy, Slow, Failing, Down or Refusing - with when it goes back to Healthy and who changed it. |

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
| 6 | Sign-in returns only to this app's one page | A crafted link could bounce the person to a phishing site | Open redirect (CWE-601) | [return-url.ts](lib/server/return-url.ts) |
| 7 | Front- and back-channel logout end the session | A session would survive signing out of the platform | Session reuse after logout | `app/api/auth/*-logout` |
| 8 | The simulator controls are shown only when the Journey API reports them enabled (Development / test), and every change is decided and recorded there | A production screen could switch off a dependency | Self-inflicted outage | [page.tsx](app/v1/operations/platform/view-all/page.tsx), the Journey API's README |
| 9 | Step-up for risky actions: `GET /api/auth/step-up` asks the IDP (`prompt=login`, `acr_values=mfa`) for a fresh authenticator code and renews this session's tokens; when the API answers 403 `mfa_required` the screen asks the Shell, which shows it in an in-page overlay (`?in=shell`: it ends on the Shell's own fixed done page); outside the Shell a small window ends on `/api/auth/step-up-done` - fixed addresses, never taken from the request - and the change is retried once | The person would have to sign out and in again; a redirect target taken from the request could be abused | Friction that pushes people to weaken MFA; open redirect | [step-up/route.ts](app/api/auth/step-up/route.ts), [oidc.ts](lib/server/oidc.ts); [step-up.ts](lib/step-up.ts), the simulator change in [page.tsx](app/v1/operations/platform/view-all/page.tsx) |
| 10 | Parked messages card on Platform Health: resend needs a reason of 10-500 characters, checked here and again downstream; a 403 `mfa_required` opens the step-up window and retries once | A resend without a recorded reason, or a resend loop | Repudiation; OWASP A04 (insecure design) | [ParkedMessagesCard.tsx](app/v1/operations/platform/view-all/ParkedMessagesCard.tsx), [actions.ts](app/v1/operations/actions.ts) |