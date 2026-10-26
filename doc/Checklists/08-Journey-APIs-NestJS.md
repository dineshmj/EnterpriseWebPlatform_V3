# 08 — Journey APIs (NestJS)

Deployables: `src/Microservices/Audit/JourneyApi`, `src/Microservices/Operations/JourneyApi`, `src/Microservices/PartnerProducts/JourneyApi`

## 1. Who may call

- [x] **Every request needs a JWT access token from the IDP.** It is verified with `jose` against the IDP's keys: issuer, audience, lifetime, signature, and `typ: 'at+jwt'` (RFC 9068), the same as the .NET APIs. → `src/auth/delegated-token.guard.ts`
- [x] **Only delegated tokens are accepted.** The token must have:
  - a person (`sub`);
  - the endpoint's scope;
  - this API's web BFF as acting client (`act.client_id`);
  - the endpoint's permission.

  *Because* a stolen machine token, or another client holding the scope, must not get in.
- [x] **Deny by default:** an endpoint without an access rule answers 403. → Operations `delegated-token.guard.ts`
- [x] **Step-up:** a risky change (the simulator switch) needs a recent MFA sign-in (`amr` + `auth_time`); otherwise it gets 403 `mfa_required`.

## 2. Calls further down

- [x] **The second token exchange:** one token per downstream scope, with this API added to the `act` chain. An IDP refusal becomes 403, an IDP outage 503. → Audit `src/services/token-exchange.service.ts`
- [x] **Downstream answers are translated** (401 / 403 → 403, 404 → 404, anything else → 503), with timeouts on every call. *Because* raw downstream errors would leak internals. → Audit `src/services/domain-api.client.ts`
- [x] **Customer facts are read again, never taken from the screen** (Partner Products: name, mobile and KYC status from Customer Onboarding for every Form VII; the paying account from Accounts). → Partner Products `src/digital-gold/digital-gold.service.ts`
- [x] **A payment is started AS THE PERSON** (Partner Products: `payments.write` exchanged for the staff member, Payments then requires her own `payment.initiate`; payee, amount and Idempotency-Key from the Partner Products API, never the screen). → Partner Products `buy` in `digital-gold.service.ts`
- [x] **A long-running follow-up is handed to a workflow engine with a business reference only** (Partner Products: Temporal workflow `gold-purchase-<number>`; no token or account number in its history; a second start is "already running"). → Partner Products `src/services/purchase-workflow.starter.ts`
- [x] **Calls that go on to another organisation wait longer (40 s) than plain domain calls (15 s)**, so the domain API's own "not reachable, stays SENT" answer is passed on. The holding read from the Partner Bank gets 25 s (inside the web BFF's 30 s for reads). → Partner Products `postViaPartnerBank` / `getViaPartnerBank` in `src/services/domain-api.client.ts`
- [x] **The simulator switch exists only when `OPERATIONS_SIMULATOR_CONTROLS_ENABLED` (Development or Test);** otherwise it answers 404. It calls loopback-only endpoints (in a cluster: with the simulators' admin key, `OPERATIONS_JOURNEY_SIMULATOR_ADMIN_KEY`), logs the LAN ID and reverts automatically. → Operations `src/platform/platform.services.ts`
- [x] **The fault routes exist only when `OPERATIONS_FAULT_CONTROLS_ENABLED` (Development, Test or UAT; the API refuses to start with it on elsewhere);** otherwise 404. The proxy's admin address must be loopback http; only known routes and states reach it; LAN ID logged, reopened after 15 minutes and at start-up. The proxy is behind a port (`FaultController`), Toxiproxy is one adapter. → Operations `src/faults/`

## 3. Abuse and errors

- [x] **Rate limits:**
  - overall: 600 a minute;
  - per person and client: 120 reads a minute, and 10 changes a minute (Operations);
  - 429 with `Retry-After`.

  The counters are per instance, and expired windows are swept every minute. → `src/http/rate-limit.ts`
- [x] **Errors are RFC 9457 Problem Details** with a safe `detail` (and a `code` where the screen must react). Unexpected errors are logged, not returned. → `src/http/problem-details.filter.ts`
- [x] **Headers:** `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `nosniff` and `Cache-Control: no-store`. HTTPS only. → `src/main.ts`

## 4. Configuration

- [x] **Fail closed:** every security setting (audience, caller client, environment) is required, with no defaults. → `src/configuration/journey-options.ts`
- [x] **Operations refuses to start outside Development with MFA off,** and outside Development and Test with the simulator switch on. Switches must be exactly `true` or `false`. → `assertSafeOutsideDevelopment`

## 5. Production (open)

- [ ] **Shared token fixtures.** The guard and the .NET handlers pass the same fixtures (R-11; the parity checklist is in Threat-Model §4, TB3).
- [ ] **Secret store.** Client secrets and TLS passwords come from a secret store (R-10).
- [ ] **Deployment-wide rate limits.** A gateway or shared store enforces them across instances (R-14).