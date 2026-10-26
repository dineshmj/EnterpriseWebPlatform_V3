# 05 — Microservice MFE BFFs (.NET)

Deployables: `src/Microservices/{CustomerOnboarding,Compliance,Accounts,Payments}/BFF.Web` (ASP.NET Core + Duende BFF) · Database: `EwpBffStateDb`, one schema and one login each

## 1. Sign-in (OIDC client)

- [x] **Each BFF is a confidential client using Authorization Code + PKCE.** The code is exchanged on the server with the client secret, which is read from configuration; without it the BFF does not start.
- [x] **Its scopes are its own API's read and write scopes,** plus only the read scopes it needs. For example, Payments adds `accounts.read` (and `accounts.write`, which the Accounts API admits for this client's cash deposits only). *Because* a stolen token should reach as little as possible.
- [x] **Tokens are kept in the server-side session** (`SaveTokens`) and refreshed there; they are never sent to the browser.
- [x] **Claim names are kept as issued** (`MapInboundClaims = false`).
- [x] **Step-up:** `/api/auth/step-up` sends `prompt=login` with `acr_values=mfa` and returns only to the fixed `/api/auth/step-up-done`. → `Controllers/AuthController.cs`
- [x] **Silent sign-in inside the Shell's frame uses `prompt=none`.** Without an IDP session, the frame shows "signed out".

## 2. Cookies

- [x] **The session cookie `__Host-Microservice-<Context>-bff` is HttpOnly, Secure, SameSite=Lax and `Path=/`; it lasts 30 minutes and slides.** *Because* script cannot read it, it never travels over HTTP, and it is not sent on cross-site sub-requests. → `Program.cs` (the cookie name is set there)
- [x] **Only the correlation and nonce cookies are SameSite=None (with Secure),** and only for the sign-in round trip.
- [x] **The anti-forgery cookie `__Host-<Context>-Bff-CSRF` is HttpOnly, Secure and SameSite=Lax.** *Because* the SPA gets the request token from `/api/auth/csrf`, never from the cookie, so script never needs it and an XSS bug cannot read it.

## 3. Requests

- [x] **Every state-changing action needs the anti-forgery header `X-CSRF-TOKEN`** (`[ValidateAntiForgeryToken]`), on top of Duende's own check (`UseBff()` before authorization).
- [x] **A thin facade:** the BFF forwards with the person's token and relays the API's answer. It holds no business rules and coordinates no saga. *Because* a BFF with business rules becomes a distributed-monolith hub. → `Controllers/*Controller.cs`
- [x] **API calls get 401, never a redirect,** when the session has ended (`/bff/api` paths). *Because* fetch cannot follow a cross-site redirect.
- [x] **Retries are for GETs only** (`DisableForUnsafeHttpMethods`), each attempt has a 10 s timeout, and a circuit breaker protects each API. An open circuit answers 503 at once. *Because* a POST (a payment, a decision) must never be repeated automatically.
- [x] **Per-caller rate limits** on `/bff` and `/api` (429 with `Retry-After`).
- [x] **Return URLs come from an allow-list:** only this MFE's own pages. *Because* an open redirect is a phishing tool. → `Configuration/BffRouteCatalog.cs`
- [x] **Customer Onboarding exchanges the agent's token (RFC 8693) for a short-lived `documents-management.write` token** to upload evidence. → `Services/DocumentsManagementTokenService.cs`

## 4. Sessions and keys

- [x] **Sessions are server-side** in PostgreSQL. Front-channel logout clears this BFF's cookie (`SignOutScheme`), and back-channel logout ends the session.
- [x] **Data Protection keys are in this BFF's own schema, with a fixed application name.** *Because* every instance reads every cookie, and no other BFF can.

## 5. Headers

- [x] **A Content-Security-Policy on every response, against XSS:**
  - scripts and styles only from self or by SHA-256 hash, never `'unsafe-inline'`;
  - `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`.
- [x] **`frame-ancestors` is the Shell and the IDP only,** against clickjacking. Any other site framing the MFE is blocked.
- [x] **Also sent:** `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`.
- [x] **HTTPS only** (HSTS outside Development), and `AllowedHosts` set.

## 6. Production (open)

- [ ] **Secret store.** Client secrets and database passwords come from a secret store (R-10).