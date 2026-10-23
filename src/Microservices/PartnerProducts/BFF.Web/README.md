# Partner Products web (Next.js SPA + light BFF)

The counter's **Digital Gold** screen, built in the customer's pattern - like the Audit and Operations web apps: one Next.js application that is both the SPA and a light BFF, running as a Node server. EWP acts as the Partner Bank's **distributor** here: the customer signs a paper form at the counter, the staff member records it with its scan; the customer never signs in at the Partner Bank on this computer.

```
Partner Products SPA (browser, in the Shell's frame)
--server action--> this app's server side (session, tokens)
--token exchange (act = this app)--> Partner Products Journey API (NestJS)
--> Customer Onboarding, Accounts, Documents Management and the Partner Products API, each with its own exchanged token
```

URL: `https://ewp-partner.dev.localhost:46384`. Runs outside Visual Studio: `runnow.bat`, or `.\ps\run\Start-NodeServices.ps1 -Only PartnerProducts`. Menu: **Partner Products -> Digital Gold** (customer service agents - `sophie.cs`, `mia.cs`).

## The page

| Page | What |
|---|---|
| `/v1/partner-products/digital-gold/view-all` | **Find the customer** (their branch's customers). **The customer**: EWP's record (KYC status, mobile ending, paying accounts) and their Digital Gold. **Form VII** (no account yet): the form's number, date signed, date of birth, the two checks (photo ID, signature), the scan. **Transfer authorization slip** (account open): slip number, amount, paying account, phone number on the slip, date (today), the check, the scan. **Form VII recorded at your branch**: those still to be sent to the Partner Bank first. While a slip waits for its payment, the slips list updates by itself (every 5 s) and the customer is reloaded once when a purchase moves on. |

Sending Form VII to the Partner Bank (which opens the account) is step 7; until then a recorded Form VII shows "Recorded - to be sent to the Partner Bank" and no slip can be recorded.

## How it works

- **Server actions** (`app/v1/partner-products/actions.ts`) are the browser's ajax calls: each reads the session, refreshes the person's token if needed, exchanges it for a short-lived token for the Journey API - `partner-products-journey.read`, or `.write` to record a form - and calls it. The browser never holds a token.
- **The scan** travels in the server action (at most 6 MB per action, 5 MB per scan) and on to the Journey API as multipart; only the known fields and one file are passed on.
- **Sessions** in PostgreSQL (`EwpBffStateDb`, schema `partner_products_bff`, user `ewp_partner_products_web`): the SHA-256 of the session cookie (never the cookie) and the tokens encrypted with AES-256-GCM (`PARTNER_PRODUCTS_WEB_SESSION_KEY`, not in the database). 30 minutes sliding.
- **Sign-in** like every MFE: silently from the Shell's frame (authorization code + PKCE), front- and back-channel logout.
- **The Shell's workspace**: the customer opened here becomes the workspace root (`Customer Name`, `Customer Number` - the same titles as Customer Onboarding and Payments, so the Shell keeps one customer across them) and the Digital Gold account, or its application until it is open, the current item (`publishSelection` in [MfeShell.tsx](app/components/MfeShell.tsx)).

## First run

1. The development certificate exported once (see the Audit Journey API's README).
2. Hosts file: `ewp-partner.dev.localhost`, `partner-products-journey.dev.localhost`, `partner-products-api.dev.localhost` (see `Sample 'hosts'.txt`).
3. The databases: `EwpPartnerProductsDb` new, `EwpIdentityAccessDb` (client, permissions), `EwpBssShellDb` (menu), `EwpBffStateDb` (`partner_products_bff`) - `Initialize-EwpDatabases.ps1 -Database ...` or Reset level 1.
4. Start the Journey API first, then this app (`Start-NodeServices.ps1` does both, in order).

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Tokens only on the server; the browser calls server actions, which Next.js accepts only from this app's own origin | Any XSS bug could read the tokens; another site could invoke the actions | Token theft; cross-site action calls | [actions.ts](app/v1/partner-products/actions.ts) |
| 2 | Each call exchanges the person's token for one aimed at the Journey API, with only the scope it needs (read, or write to record a form) | The Journey API could not tell which application calls for the person; a read would carry write rights | Confused deputy; excessive privilege | `exchangeForJourneyApi` in [oidc.ts](lib/server/oidc.ts) |
| 3 | Only the known form fields (strings of at most 100 characters) and one scan of at most 5 MB are passed on; server actions are capped at 6 MB | A crafted form could smuggle fields to the APIs behind; a huge upload could exhaust the server | Mass assignment; resource consumption | `recordSigned` in [actions.ts](app/v1/partner-products/actions.ts), [next.config.ts](next.config.ts) |
| 4 | The customer's identity is never typed on the screen - name, mobile and KYC status come from EWP's record (read again on the server); the staff member enters only what is on the paper form and confirms the checks | A staff member could record a form under someone else's details | Identity fraud at the counter | [page.tsx](app/v1/partner-products/digital-gold/view-all/page.tsx), the Journey API's README |
| 5 | Sessions in PostgreSQL keyed by the cookie's SHA-256, tokens AES-256-GCM encrypted with a key outside the database | A database reader could hijack or read a session | Session hijacking; token theft from storage | [session-store.ts](lib/server/session-store.ts) |
| 6 | Cookies `__Host-`, HttpOnly, Secure, SameSite | Script-readable or cross-site cookies | Session theft, CSRF | [config.ts](lib/server/config.ts) |
| 7 | CSP with a per-request nonce; framed only by the Shell (`frame-ancestors`; the Shell and IDP origins are required settings); the Shell protocol only with the Shell's static origin | Injected script would run; any site could frame the screen | XSS; clickjacking; message spoofing | [proxy.ts](proxy.ts), [MfeShell.tsx](app/components/MfeShell.tsx) |
| 8 | Sign-in returns only to this app's one page | A crafted link could bounce the person to a phishing site | Open redirect (CWE-601) | [return-url.ts](lib/server/return-url.ts) |
| 9 | Front- and back-channel logout end the session | A session would survive signing out of the platform | Session reuse after logout | `app/api/auth/*-logout` |
| 10 | While a slip waits for its payment (ORDERED / PAID), the screen follows it with a light read every 5 s that does NOT extend the session (`readSession(..., { slide: false })`), not while the tab is hidden, at most 30 minutes | A forgotten tab would keep the session alive for ever, defeating the 30-minute idle timeout | Session left open on an unattended counter PC | `loadPurchases` in [actions.ts](app/v1/partner-products/actions.ts), [session-store.ts](lib/server/session-store.ts), [page.tsx](app/v1/partner-products/digital-gold/view-all/page.tsx) |
| 11 | Shell–MFE messages go to, and are accepted from, the Shell's origin only - the Shell's origin is this BFF's own setting, read at run time (`/ewp-runtime-config.js`, [shell-origin.ts](lib/shell-origin.ts)) - never built into the front end; missing or not https = no message is sent or accepted | A hostile page that frames the MFE could pose as the Shell; a Shell origin built into the image would tie it to one environment | Cross-origin message spoofing (CWE-346) | [MfeShell.tsx](app/components/MfeShell.tsx), [route.ts](app/ewp-runtime-config.js/route.ts), [layout.tsx](app/layout.tsx) |