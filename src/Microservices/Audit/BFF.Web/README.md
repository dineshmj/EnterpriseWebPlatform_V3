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

## Not yet

- Structured logs, metrics and traces in the Node tier.
- The Shell's workspace context (the audit screens neither read nor publish a customer).