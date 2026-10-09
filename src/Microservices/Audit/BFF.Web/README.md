# Audit web (Next.js SPA + light BFF)

The auditor's front end, and the **first front end built in the customer's pattern**: one Next.js application that is both the SPA and a light BFF, running as a Node server (not a static export served by an ASP.NET Core BFF like the other MFEs).

```text
Audit SPA (browser, in the Shell's frame)
  --ajax (server action)--> THIS app's server side: session, tokens, token exchange
  --token exchange (act = this app)--> Audit Journey API (NestJS)
  --token exchange (act = Journey API, act = this app)--> Audit API, Payments API
```

Like every context's front end it lives in `<Context>\BFF.Web`; unlike the others it has no `client-app` subfolder, because the Next.js app IS the BFF - its server side (route handlers and server actions) signs in, holds the session and exchanges tokens.

URL: `https://audit.dev.localhost:46380`. Runs outside Visual Studio: `runnow.bat`. Menu: **Audit Trail** (auditors only - `sarah.audit`).

## Screens

| Path | What |
|---|---|
| `/v1/audit/trail/view-all` | Search the trail by record or customer number, person (LAN ID), event, dates; switch to **Who read the trail** (`ACCESS` entries). **Verify integrity** re-walks the hash chain. |
| `/v1/audit/records/view-details?recordRef=` | One record end to end: **where it stands now** (for a payment, from the Payments API) and **what happened** (the timeline, with each entry's hash and the original message's SHA-256). |

Every search and every record opened is itself recorded in the trail.

## The light BFF (server side)

- **Sign-in** like every MFE: the Shell points the frame at `/api/auth/silent-login?returnUrl=<page>` (allow-listed); authorization code + PKCE with `prompt=none` against the IDP's SSO session (`openid-client`). The sign-in asks for identity and refresh only - the token is good for nothing but being exchanged.
- **Server actions** (`app/v1/audit/actions.ts`) are the browser's ajax calls: each reads the session, refreshes the person's token if needed, exchanges it for a short-lived token for the Journey API, and calls it. The browser never holds a token; Next.js accepts a server action only from this app's own origin.
- **Sessions** in PostgreSQL (`EwpBffStateDb`, schema `audit_bff`, user `ewp_audit_web`): a restart signs nobody out. The table holds the SHA-256 of the session cookie (never the cookie) and the tokens encrypted with AES-256-GCM (`AUDIT_WEB_SESSION_KEY`, not in the database), so a database reader can neither hijack nor read a session. 30 minutes sliding.
- **Logout** is the Shell's: the IDP's front-channel iframe (`/api/auth/frontchannel-logout`) and signed back-channel logout token (`/api/auth/backchannel-logout`) end the sessions of the IDP session.
- **Security headers** (`proxy.ts`): a strict Content-Security-Policy with a per-request nonce on every script (`'strict-dynamic'`), framed only by the Shell and the IDP; `nosniff`; referrer policy.

## Run it

1. Once: the development certificate in your profile (see the [Journey API README](../JourneyApi/README.md#run-it)).
2. Hosts file: `127.0.0.1    audit.dev.localhost`.
3. `.\ps\run\Start-NodeServices.ps1` in the repository root starts it with the other Node services (after the Journey API). Or by hand: `..\JourneyApi\runnow.bat`, then `runnow.bat` here (installs, builds, starts). Every setting is an environment variable (`lib/server/config.ts`); the development values are in `runnow.bat`, elsewhere from a secret store.
4. Debugging: `runnow.bat dev` in VS Code's **JavaScript Debug Terminal** runs Next.js in development mode (no build, source maps, reload on save): breakpoints in server actions, route handlers and `lib/server` are hit; client code is debugged in the browser's DevTools.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens only on the server; the browser calls server actions, which Next.js accepts only from this app's own origin | Any XSS bug could read the tokens; another site could invoke the actions | Token theft; cross-site action calls | [actions.ts](app/v1/audit/actions.ts) |
| 2 | Authorization code + PKCE, `state`, `nonce`; the sign-in token carries no API scope (it is good only for being exchanged) | A stolen code could be redeemed; a stolen sign-in token could call APIs directly | Code interception; token misuse | [oidc.ts](lib/server/oidc.ts) |
| 3 | Token exchange (RFC 8693) for a short-lived token aimed at the Journey API only, with this app as `act` | A broad token could be replayed at any API, and the API could not tell who is calling | Token replay across services; loss of the caller chain | [oidc.ts](lib/server/oidc.ts), [TokenExchangeGrantValidator.cs](../../../IDP/Security/TokenExchangeGrantValidator.cs) |
| 4 | Sessions in PostgreSQL keyed by the SHA-256 of the cookie, tokens encrypted with AES-256-GCM (key not in the database) | Anyone who can read the table could hijack sessions or read tokens | Session hijacking from a database leak | [session-store.ts](lib/server/session-store.ts), [EwpBffStateDb.sql](../../../../db/EwpBffStateDb.sql) |
| 5 | Strict Content-Security-Policy with a per-request nonce for scripts (`'strict-dynamic'`) and styles; framed only by the Shell and the IDP; `nosniff`, referrer policy | Injected scripts or styles would run; any site could frame the screens | Cross-site scripting, CSS injection, clickjacking | [proxy.ts](proxy.ts) |
| 6 | Sign-in returns only to allow-listed pages | A crafted link could bounce the auditor to a phishing site | Open redirect (CWE-601) | [return-url.ts](lib/server/return-url.ts) |
| 7 | Front-channel and signed back-channel logout | A session would survive signing out in the Shell | Session reuse after logout | [backchannel-logout/route.ts](app/api/auth/backchannel-logout/route.ts), [frontchannel-logout/route.ts](app/api/auth/frontchannel-logout/route.ts) |
| 8 | Shell–MFE messages accepted only from the Shell's origin and the parent window; own not-found page | A hostile framing page could pose as the Shell | Cross-origin message spoofing | [MfeShell.tsx](app/components/MfeShell.tsx) |
| 9 | Every setting from the environment, required; development values only in `runnow.bat` | Defaults could ship to production | Leaked defaults | [config.ts](lib/server/config.ts) |

## Not yet

- Structured logs, metrics and traces in the Node tier.
- The Shell's workspace context (the audit screens neither read nor publish a customer).