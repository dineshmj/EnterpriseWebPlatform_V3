# 06 — KYC BFF (NestJS)

Deployable: `src/Microservices/CustomerKyc/BFF.Web` (NestJS + `openid-client`) · Database: `EwpBffStateDb` schema `kyc_bff` (login `ewp_kyc_bff`)

## 1. Sign-in (OIDC client)

- [x] **A confidential client using Authorization Code + PKCE, with state and nonce.** Silent sign-in in the frame uses `prompt=none`; step-up uses `prompt=login` with `acr_values=mfa`. → `src/auth/oidc.service.ts`
- [x] **The session ID is regenerated at sign-in,** against session fixation. → `oidc.service.ts` (`req.session.regenerate`)
- [x] **Refreshes are single-flight:** parallel requests share one refresh. *Because* two refreshes with a one-time refresh token would revoke each other.
- [x] **The OIDC round-trip state** (state, nonce and PKCE verifier) **lives in a short-lived cookie that is encrypted with AES-256-GCM, under a key derived with HKDF.** It is HttpOnly and used once. → `src/auth/pending-sign-in.ts`
- [x] **Back-channel logout verifies the signed logout token** (issuer, audience, signature, the logout event, no nonce) and deletes every session of that IDP session. → `src/auth/backchannel-logout.controller.ts`

## 2. Cookies and sessions

- [x] **The session cookie `__Host-KYC-BFF-SESSION` is HttpOnly, Secure, SameSite=Lax and `Path=/`; it lasts 30 minutes and rolls.** → `src/main.ts`
- [x] **Sessions are stored in PostgreSQL.** Only the SHA-256 of the session ID is stored, and the session, tokens included, is encrypted with AES-256-GCM. The 32-byte key is not in the database. *Because* a database reader can neither hijack nor read a session. → `src/auth/session-store.ts`

## 3. Requests

- [x] **Every write needs the `x-csrf-token` header,** compared in constant time (`timingSafeEqual`) with the session's token. → `src/auth/csrf.ts`, `src/controllers/kyc-cases.controller.ts`
- [x] **Decisions are sent once (`postOnce`):** no automatic retry of a POST.
- [x] **GETs go through a client with a timeout, retries and a circuit breaker per downstream.** → `src/resilience/resilient-fetch.ts`
- [x] **Documents are read with an exchanged `documents-management.read` token** (RFC 8693), cached per person until shortly before it expires. → `src/services/documents-management-token.service.ts`
- [x] **Document IDs are checked against a strict pattern** before any call. → `src/services/documents-management.service.ts`
- [x] **Documents are served with the VERIFIED content type and `nosniff`.** Only a PDF is shown inline; everything else is a download. *Because* an upload must never run as HTML (stored XSS). → `kyc-cases.controller.ts`

## 4. Headers and configuration

- [x] **A Content-Security-Policy on every response, against XSS.** Scripts and styles come from self or by SHA-256 hash, never `'unsafe-inline'`, and `frame-ancestors` is the Shell and the IDP only. → `src/security/content-security-policy.ts`
- [x] **Also sent:** `X-Content-Type-Options: nosniff`. HTTPS only.
- [x] **Fail-closed configuration:** every required setting (secret, session key, audience) is read at start-up, and a missing one stops the BFF. → `src/configuration/kyc-bff-options.ts`
- [x] **The same log format as the .NET services:** a trace ID on every line, and no query strings. → `src/observability/logger.ts`

## 5. Production (open)

- [ ] **Secret store.** The client key (private_key_jwt) and the session key come from a secret store; `runnow.bat` values are for Development only (R-10).
- [ ] **Shared token fixtures.** The token rules are tested against fixtures shared with the .NET stack (R-11).