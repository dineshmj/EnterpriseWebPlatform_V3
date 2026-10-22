# EWP V3 — Threat Model

**What this is.** The living threat model of the EWP V3 banking platform: what is worth protecting, who might attack it, where the trust boundaries are, which threats apply at each boundary (STRIDE), which control mitigates each one, and what risk remains. Every control named here is implemented and listed, with its code location, in the **"Security controls"** table of the component's README. This document links them into one picture.

**Status.** Proof of concept, local development deployment. Section 7 lists what remains open and what is accepted for a PoC; section 8 lists the assumptions.

**Keeping it alive.** Any change that adds, removes or weakens a security control updates the component's README table **and** the matching row here. A new component, data store, external system or integration topic adds a row to sections 2–4.

---

## 1. Assets

| Asset | Where it lives | Sensitivity | What an attacker wants |
|---|---|---|---|
| Customer identity data (name, address, contact) | EwpCustomerDb, EwpKycDb, Kafka event payloads | PII (Privacy Act 1988 / APPs) | Identity theft, harvesting |
| KYC evidence (driver licence, Notice of Assessment) | Documents Management file store + EwpDocumentsManagementDb | Highly sensitive PII | Identity fraud |
| Money and its movement (balances, holds, payments) | EwpAccountsDb, EwpPaymentsDb, payment network | Financial | Unauthorised payment, theft |
| Decisions (KYC, compliance, account, payment approvals) | Each context's database, Kafka | Regulatory evidence | Fraudulent approval, repudiation |
| Credentials and tokens (passwords, sessions, access / refresh tokens, client secrets) | IDP database, BFF session stores (EwpBffStateDb), configuration | Secret | Account and service impersonation |
| Signing keys, Data Protection key rings, MFA secrets | IDP database (encrypted), EwpBffStateDb (per BFF), certificates | Secret | Forged tokens or cookies, MFA bypass |
| Audit trail | EwpAuditDb (append-only, hash chain) | Regulatory evidence | Erasing or rewriting history |
| Workflow messages | Kafka topics, Outbox / Inbox tables | Integrity-critical | Forged or replayed steps |

## 2. Threat actors

| Actor | Capability | Typical goal |
|---|---|---|
| **External attacker** | Internet access to the public front ends; phishing; stolen passwords | Account takeover, data theft |
| **Malicious or careless insider (staff)** | A valid account with a role: agent, officer, operations, auditor | Fraudulent payment or approval, browsing customers, covering tracks |
| **Compromised workload** | Code execution in one service or worker, with its credentials | Lateral movement: other databases, topics, APIs |
| **Privileged infrastructure administrator** | Database or broker superuser, host access | Tampering with records or the audit trail |
| **Supply chain** | A malicious or vulnerable package | Code execution in the build or at run time |

## 3. Trust boundaries

```
  [Browser] ──TB1── Shell / MFE BFFs ──TB2── IDP (OIDC, token exchange)
                         │
                        TB3  (access tokens, at+jwt; BFF holds them)
                         ▼
                 Domain / Journey APIs ──TB4── PostgreSQL (one database per context)
                   │            ▲
                  TB5          TB5  (Kafka SASL/SCRAM, ACLs; Outbox → topic → subscriber → API)
                   ▼            │
                 Kafka ◄──── Workers (subscribers, relays; M2M tokens)
                   │
  APIs ──TB6── External systems (core banking, payment network, screening provider)
  Audit API ◄── Kafka (read only) ;  Auditor ──TB1/TB3── Audit BFF → Audit Journey API (token exchange)
```

| ID | Boundary | Crosses it |
|---|---|---|
| TB1 | Browser ↔ Shell and MFE BFFs | People: cookies, page content, framed MFEs, postMessage, WebSocket (notifications) |
| TB2 | BFFs ↔ IDP | OIDC sign-in, refresh, token exchange (RFC 8693), back-channel logout |
| TB3 | BFFs and workers ↔ APIs | Bearer access tokens: delegated (a person) or M2M (a worker) |
| TB4 | APIs ↔ their database | One least-privilege database user per deployable |
| TB5 | Services ↔ Kafka | Each component as its own SCRAM user, limited by ACLs to its own topics and group |
| TB6 | APIs ↔ external systems | API key + Idempotency-Key over HTTPS, resilience pipeline |

## 4. Threats and controls (STRIDE)

Abbreviations: **S** spoofing, **T** tampering, **R** repudiation, **I** information disclosure, **D** denial of service, **E** elevation of privilege. "README" refers to the component's Security controls table.

### TB1 — Browser ↔ Shell and MFE BFFs

| | Threat | Control | Where |
|---|---|---|---|
| S | Stolen password signs in | MFA (TOTP) at the IDP, with lockout and per-IP throttling; step-up for decisions; startup refuses MFA off outside Development | [IDP README](../src/IDP/README.md) #1, #3, #13; [MfaStepUp.cs](../src/Common/WebUtilities/Security/MfaStepUp.cs) |
| S/I | Token theft through XSS | Tokens never reach the browser (BFF); HttpOnly `__Host-` cookies; hash-based CSP without `'unsafe-inline'` | [Shell README](../src/Shell/README.md) #1, #2, #5; each MFE BFF README |
| T | Cross-site request forgery | Anti-forgery header on every BFF API call; SameSite=Lax; WebSocket origin check | Shell README #3, #4; KYC BFF README |
| I | Hostile frame receives workspace context or notifications | postMessage accepted only from the loaded MFE's own origin; `frame-src` limited to MFEs and IDP; MFEs trust a static parent origin | Shell README #5, #7 |
| T/E | Open redirect or injected parameters via sign-in `returnUrl` or a notification link | Allow-listed routes per BFF (exact shapes); one validated parameter in notification targets | Shell README #11; each BFF README ("allow-listed routes") |
| S | Session replay after sign-out | Server-side IDP sessions; back-channel logout ends every BFF session | IDP README #8, #10 |
| D | Flooding a BFF | Per-caller rate limits | Shell README #10; each BFF README |

### TB2 — BFFs ↔ IDP

| | Threat | Control | Where |
|---|---|---|---|
| S | Client impersonation | Confidential clients, secrets from configuration (fail closed); authorization code + PKCE only | IDP README #4, #6 |
| E | Delegation abuse: turning any token into any other | Token exchange by allow-list (who, which token, which audience); the person is re-checked; `act` claim keeps the chain | IDP README #5 |
| T | Refresh token replay | One-time refresh tokens (rotation); refresh tokens hashed in the store | IDP README #4, #7 |

### TB3 — Callers ↔ APIs

| | Threat | Control | Where |
|---|---|---|---|
| S | Token confusion (ID token or another API's token) | Issuer, audience, lifetime, signature; only `typ: at+jwt` | Every API README (token validation) |
| E | Endpoint without authorization | Deny by default, one helper for every domain API (`MapEwpApiControllers` + authenticated fallback policy); the Journey APIs refuse an endpoint without an access rule | Every API README (deny by default); `ApiEndpointAuthorization.cs` |
| E | Reading or changing another branch's records (BOLA) | Branch scope on every read and command; payment funds reserved only on an account of the initiator's branch | Payments README #4, #15; Accounts README #13; KYC / CO / DM READMEs |
| E | Privilege combination (auditor + branch role) | The auditor's permission counts only through the Audit Journey API | Payments README #4 |
| E | Insider fraud: self-approval, approval above authority; one person settling or releasing a payment of unknown outcome | Separation of duties (maker ≠ checker, across contexts); approval limits by clearance; clearance by risk (Compliance); four-eyes manual reconciliation | Payments README #5, #6, #19; Accounts README #5; Compliance README |
| S | A stolen machine token posts fake workflow steps | Internal endpoints pin the exact `client_id`, not only the scope | Every API README (internal endpoints) |
| T | Duplicate or tampered retries create a second payment | Idempotency-Key bound to the person and the payment's details (409 otherwise); unique constraints | Payments README #7, #13 |
| I | Internals in error responses | Problem Details everywhere: a trace ID only in the .NET APIs, a generic detail for unexpected errors in the Journey APIs | Every API README (problem responses) |
| D | One caller floods an API | Per-caller rate limits (person, client, IP) in the .NET APIs; overall + per person and client in the Journey APIs (changes far lower than reads) | Every API README (rate limits); Journey API READMEs |
| D/E | An operator (or a stolen session) switches off an external system | The simulators' switch exists only in Development / test (unset = off; the API refuses to start with it on, or with MFA off, outside them), needs `simulator.behaviour.manage` and a recent MFA sign-in, is logged with the LAN ID and reverts to Healthy after 15 minutes; the simulators accept it from loopback only | [Operations Journey API README](../src/Microservices/Operations/JourneyApi/README.md) |
| D/E | An operator (or a stolen session) cuts the route to one of the platform's own APIs, or reaches the fault proxy's admin API directly | The fault routes exist only in Development, Test and UAT (the Operations Journey API refuses to start with them on elsewhere; production has no proxy, its callers use direct addresses); a change needs `simulator.behaviour.manage` and a recent MFA sign-in, is logged with the LAN ID and reopens after 15 minutes; every route is opened at the Journey API's start-up and when the proxy restarts (Reset.ps1). The proxy's admin API has no authentication, so it and every route listen on 127.0.0.1 only and the Journey API accepts only a loopback admin address; TLS passes through it untouched (it never sees a token) | [Operations Journey API README](../src/Microservices/Operations/JourneyApi/README.md), [Start-Toxiproxy.ps1](../ps/toxiproxy/Start-Toxiproxy.ps1) |

#### Token rules implemented twice (.NET and NestJS) - parity checklist

The .NET APIs (JWT bearer + policies) and the NestJS Journey APIs (`delegated-token.guard.ts`) enforce the same token rules in two languages. A change to one side is not done until the other matches:

| Rule | .NET APIs | NestJS Journey APIs |
|---|---|---|
| Issuer, signature (IDP's keys), lifetime | `AddJwtBearer` | `jwtVerify` with the IDP's JWKS |
| Audience = this API | `ValidAudience` | `audience` option (no default) |
| Access tokens only | `ValidTypes = at+jwt` | `typ: 'at+jwt'` |
| Scope per endpoint | `RequireClaim("scope", ...)` | `Requires` / scope check |
| Permission per endpoint | `PaymentsStaffRequirement` etc. | `Requires` / permission check |
| Acting client pinned (delegated tokens) | `ActingClientOf` (auditor via the Audit Journey API) | `act.client_id` = the configured caller |
| Recent MFA for step-up | `MfaStepUp.IsRecent`: `amr` contains `mfa`, `auth_time` at most N minutes old, not in the future | `recentMfa`: the same rule; MFA required outside Development |
| Deny by default | `MapEwpApiControllers` + fallback policy | an endpoint without an access rule is refused |
| Rate limits, Problem Details | `UseEwpRateLimiting`, `AddProblemDetails` | `rate-limit.ts`, `problem-details.filter.ts` |

When automated tests resume, both sides get the same token fixtures (expired, wrong audience, wrong `typ`, missing or old `auth_time`, wrong `act`) and must give the same answers.

### TB4 — APIs ↔ PostgreSQL

| | Threat | Control | Where |
|---|---|---|---|
| E/I | A compromised service reads or changes other contexts' data | One database per context; one login role per deployable with data access to its own database only, no DDL | [EwpServiceDbUsers.sql](../db/EwpServiceDbUsers.sql); every API README (own database user) |
| T/R | Rewriting the audit trail | Append-only (trigger + SELECT/INSERT-only user); hash chain verified continuously; readiness degrades on a break; signed checkpoints outside the database catch a complete rewrite; readiness also degrades when verification is overdue, the newest entry is not anchored, or checkpoints go missing | [Audit API README](../src/Microservices/Audit/API/README.md) #3, #4, #8 |
| I/T | Reading or replacing KYC documents in storage | Envelope encryption at rest (per-document AES-256-GCM key, wrapped by a rotatable key-encryption key; document ID bound in); served only after authorization, magic-byte checks, attachment disposition | [DM API README](../src/Microservices/DocumentsManagement/API/README.md) #6, #12 |

### TB5 — Services ↔ Kafka

| | Threat | Control | Where |
|---|---|---|---|
| S/T | Forged events on a topic | SASL/SCRAM user per component; ACLs: each producer writes only its own topics | [kafka/README.md](../kafka/README.md); [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1) |
| T | Lost or duplicated workflow steps (dual write, redelivery) | Transactional Outbox; Inbox (idempotent consumers); per-aggregate ordering; dead-letter topics | Subscribers README; each API README |
| T | Stale or out-of-order saga replies | The saga accepts a reservation / settlement reply only for its current command (CausationId) | Payments README #14 |
| I | Sensitive data in logs shipped elsewhere | No full account numbers in logs (masked) | Payments README #16; Accounts README #14 |

### TB6 — APIs ↔ external systems

| | Threat | Control | Where |
|---|---|---|---|
| T | A retried call pays or opens twice | Idempotency-Key on every call; a refusal is final | Payments README #7; Accounts README #6 |
| D | A slow or failing provider cascades | Timeouts, retries with jitter, circuit breaker (standard resilience handler); saga timeouts and compensation | Each API's `Program.cs`; Payments README |
| S/I | Calls to an impostor endpoint; SSRF by configuration | Base URLs checked at start-up (outside Development: https, `PermittedHosts`, no internal address); no redirects followed; API keys; HTTPS | Payments README #18; Accounts README #15; Compliance README #13; `ExternalEndpoint.cs` |
| S/T/R | The Partner Bank (EWP as distributor): an impostor, a replayed token, a denied or altered instruction, a second account or order after a lost answer | Mutual TLS with the PoC's own root; EWP's certificate-bound token; every instruction signed (ES256, `jti` fixed at the first send); payment advice and cancellation only for EWP's own orders | Partner Products API README #11-#15, #18 |

## 5. Abuse cases (banking)

"Verified by" names the automated test that fails if the control breaks: **UT** = .NET unit or adapter test in `tst/dotnet` (project / class), **PW** = Playwright spec in `tst/playwright/tests`. "Code only" means there is no automated test yet.

| Abuse case | How the platform stops it | Verified by |
|---|---|---|
| An agent pays from another branch's customer by calling the API directly | Accounts refuses the reservation (`ACCOUNT_NOT_IN_BRANCH`) | UT `Accounts.Api.Tests` / `Account_RefusalToHold_Edge_Reasons`; PW `40-security/api/out-of-branch-payment.spec.ts` |
| A payments officer approves their own payment | Separation of duties in the aggregate | UT `Payments.Api.Tests` / `PaymentSaga_OnApproved_Edge_LimitsAndSeparationOfDuties` |
| An officer approves above their clearance | Approval limits by clearance | UT `Payments.Api.Tests` / `PaymentSaga_OnApproved_Edge_LimitsAndSeparationOfDuties`, `ApprovalLimits_Allows_Edge_ClearanceLevels` |
| A stolen password approves a payment | Step-up MFA on decisions, signed in within the last 15 minutes (MFA on outside Development) | PW `40-security/mfa/step-up-without-sign-out.spec.ts` (runs with MFA on) |
| An intercepted or shoulder-surfed MFA code is used again | A code is claimed once with one conditional UPDATE (the time step only moves forward); recovery codes once; five wrong codes lock the account for 15 minutes | UT `IDP.Tests` / `MfaService_VerifyAsync_Edge_OneTimeCodes` |
| A client turns a person's token into a token for an API it should not reach | Token exchange by allow-list (which client, which token, which audience); machine tokens carry no person and are refused | PW `40-security/api/token-exchange-allow-list.spec.ts` |
| An auditor browses payments unrecorded | Only through the Audit Journey API, where every look is itself audited | UT `Payments.Api.Tests` / `PaymentsStaffAuthorizationHandler_Edge_PermissionsAndBranch`; PW `40-security/api/authorization-boundaries.spec.ts`, `40-security/audit/audit-trail-access.spec.ts` |
| A forged "funds held" message moves a payment | Kafka ACLs + pinned `client_id` on internal endpoints; the saga accepts a reply only for its current command | UT `Payments.Api.Tests` / `PaymentSaga_Replies_Edge_CausationMatching`; PW `40-security/api/worker-internal-endpoints.spec.ts`. ACLs: [Setup-KafkaSecurity.ps1](../ps/kafka/Setup-KafkaSecurity.ps1), not tested |
| A slow network makes a retry pay twice | Idempotency-Key at the network; the same key with other details is refused (409) | UT `Payments.Api.Tests` / `InitiatePaymentCommandHandler_HandleAsync_Edge_Idempotency`; PW `35-resilience/payments/payment-network-resilience.spec.ts` (paid once), `40-security/api/payments-idempotency.spec.ts` |
| An admin rewrites the audit trail | Detected by the hash chain; a complete recomputation is detected by the signed checkpoints kept outside the database | PW `40-security/audit/audit-tamper-evidence.spec.ts` |
| An agent records a Form VII for a person who never signed one, or under another customer's details (Partner Products) | The customer's name, mobile and KYC status are read again from Customer Onboarding for the agent (their branch), never typed; the scan of the signed form is required and its SHA-256 recorded; the agent confirms photo ID and signature; one application per customer; from step 7 the Partner Bank also tells the customer by SMS | Code only (Partner Products API README #3-#6, Journey API README #3); PW `30-journeys/partner-products/digital-gold-journeys.spec.ts` covers the accepted path only |
| A transfer slip debits an account that is not the customer's, or is reused (Partner Products) | The paying account must be one of the customer's active accounts (Accounts, for the agent); the slip's phone number must be the mobile on record; signed today; one purchase per slip (unique index) | UT `PartnerProducts.Api.Tests` / `GoldPurchase_Record_Edge_SlipRules` (open account, today, mobile, signature, amount). The account check and one purchase per slip: code only (Partner Products API README #5-#6, Journey API README #4) |
| A Digital Gold purchase pays someone other than the Partner Bank, or pays twice (Partner Products) | The payee is the Partner Bank's settlement account from configuration, never typed; amount and account are the slip's; one Idempotency-Key per slip (unique), fixed when the slip is recorded; the payment is started as the agent, so Payments' own permission, approval tier and four eyes apply | UT `PartnerProducts.Api.Tests` / `GoldPurchase_Record_Edge_SlipRules` (the payment is exactly the slip, one payment per order); PW `30-journeys/partner-products/digital-gold-journeys.spec.ts` ($20 ALLOCATED) |
| A link to a customer's account at the Partner Bank is copied, shared or replayed, or opens more than one account (Partner Products) | "View at the Partner Bank" is a PAR-style handoff: a new signed staff view per click (one account, its customer, the staff member from their token) pushed over mutual TLS; the browser gets only a random one-time `request_uri` (60 s) on the CONFIGURED Partner Bank page; the Partner Bank shows it once, read-only, no-store, naming the staff member | PW `30-journeys/partner-products/digital-gold-journeys.spec.ts` (staff view opens once); Partner Bank repo: UT `PartnerBank.Api.Tests` / `StaffViews_PushAndRedeem_Edge_OneUse` (pushed twice, redeemed twice, 32 at once), `tools/JourneyCheck` section 9 |
| Gold is allocated without a payment, or a paid order is cancelled (Partner Products) | Only the Partner Products Worker's pinned machine token reaches the follow-up endpoints; PAID only for the purchase's own completed payment (key, amount, payee checked), saved before the advice; CANCELLED only while unpaid; the Partner Bank accepts the advice only for the exact amount, once | UT `PartnerProducts.Api.Tests` / `GoldPurchase_MarkPaid_Edge_OnlyItsOwnPayment`, `GoldPurchase_MarkCancelled_Edge_OnlyWhileUnpaid`; PW `40-security/api/worker-internal-endpoints.spec.ts`, `30-journeys/partner-products/digital-gold-journeys.spec.ts` (network Refusing → CANCELLED) |
| The Partner Bank adapter accepts an altered answer, or a lost answer becomes a second order (Partner Products) | Every instruction signed (ES256) with a `jti` fixed at the first send; 4xx = the Partner Bank's final refusal, 5xx / time-out / unreachable = unavailable (send again with the same `jti`) | UT `PartnerProducts.Api.Tests` / `PartnerBankClient_PlacePurchaseAsync_Happy_SignedInstruction`, `PartnerBankClient_PlacePurchaseAsync_Edge_LostAndRefusedAnswers` (WireMock.Net). Mutual TLS on the wire: not covered |

## 6. Security testing

| Layer | Where |
|---|---|
| End to end (journeys, deciders, audit trail) | `tst/playwright/tests/30-journeys` (onboarding, payments, Digital Gold) |
| Resilience (external systems refuse / are down / are slow; the platform's own APIs cut at the fault proxy) | `tst/playwright/tests/35-resilience` |
| Security (headers, sessions, MFA step-up, token exchange, internal endpoints, branch scope, idempotency, audit tamper evidence) | `tst/playwright/tests/40-security` |
| Unit tests (aggregates, authorization handlers, decision branches; the IDP's one-time MFA codes on SQLite) | `tst/dotnet` (`EwpTests.slnx`): Accounts, Payments, Partner Products, IDP |
| Adapter tests (neighbours played by WireMock) | `tst/dotnet/PartnerProducts.Api.Tests` (WireMock.Net); Partner Products Journey API `pnpm test` (Jest + WireMock standalone) |
| The Partner Bank's side (signature check, once-only instructions, staff view one use) | Partner Bank repo: `tst/PartnerBank.Api.Tests` (unit tests, in-memory SQLite) and `tools/JourneyCheck` (end to end); its `doc/Threat-Model.md` section 5 |

## 7. Residual risks

| ID | Risk | Rating | Status | Verified by |
|---|---|---|---|---|
| R-1 | The payment network call ran inside a database transaction (row lock held up to 20 s) | Medium (availability) | **Fixed**: claim (short transaction, 60 s lease) → call with no transaction → record (short transaction, only if the saga's Version is unchanged) — `RunDueSagaStep.cs` | PW `35-resilience/payments/payment-network-resilience.spec.ts` (slow network: paid once) |
| R-2 | KYC documents were not encrypted at rest | High (PII) | **Fixed**: envelope encryption in `EncryptedDocumentStorage.cs`. Remaining in production: key-encryption keys in a key vault / HSM instead of configuration | Code only |
| R-3 | The audit hash chain had no anchor outside its database: a privileged administrator could recompute the whole chain | Medium | **Fixed**: signed checkpoints (ECDSA P-256) in write-once files, checked by every verification - `AuditAnchors.cs`. Remaining in production: the signing key in a key vault / HSM and the checkpoints in immutable storage under another administrator | PW `40-security/audit/audit-tamper-evidence.spec.ts` |
| R-4 | Approvals required MFA at sign-in, not a recent one | Medium | **Fixed**: step-up requires `amr` mfa AND `auth_time` at most `Mfa:StepUpMaxAgeMinutes` (default 15) old - `MfaStepUp.cs`. A stale sign-in is renewed in place (R-13) | PW `40-security/mfa/step-up-without-sign-out.spec.ts` (MFA on; the 15-minute age itself: code only) |
| R-5 | No operator tool to resend parked Outbox messages; no reconciliation with the payment network for unknown outcomes | Medium (operations) | **Reconciliation fixed**: after unanswered sends the saga asks the network by Idempotency-Key and settles or releases accordingly, never releasing blind (`PaymentSaga` CONFIRM_WITH_NETWORK). **Outbox resend fixed**: operations see parked messages on the Payment Processing Monitor and resend one (same message id, reason required, `workflow.reprocess` + recent MFA, recorded on the message and the payment's timeline); attempts and back-off are configuration (`Payments:Outbox`). Accounts, Compliance and Partner Products: the same resend from the Operations Platform Health page, through the Operations Journey API (delegated token, `act` pinned) | Reconciliation: UT `Payments.Api.Tests` / `PaymentSaga_Network_Edge_UnknownOutcome`; PW `35-resilience/payments/payment-network-resilience.spec.ts`. Outbox resend: manual run T3 (21-OCT) |
| R-6 | Audit actor fields come from producer envelopes (trusted producers, Kafka ACLs) — not signed | Low | Accepted for PoC | - |
| R-7 | SignalR hub had no per-user connection quota | Low | **Fixed**: at most `Notifications:MaxConnectionsPerPerson` (default 10) connections per person, per instance - `NotificationsHub.cs` | Manual (the 11th tab shows "Reconnecting", 12-OCT) |
| R-8 | `/metrics` and health endpoints are anonymous (network controls assumed) | Low | Accepted for PoC (deployment control) | - |
| R-9 | No mTLS between services | Medium in production | Accepted for PoC (service mesh in production) | - |
| R-10 | Development secrets and certificates in configuration (Kafka's keystore and admin passwords in `server.properties`) | High in production | Accepted for PoC: development only; production uses a secret store and fail-closed start-up. Kafka traffic is TLS-encrypted since B8 (`SASL_SSL`, broker certificate and host name checked by every client); only the controller listener stays PLAINTEXT on localhost | - |
| R-11 | Token rules are implemented twice (.NET and NestJS) and could drift | Medium | **Mitigated**: parity checklist in §4 (TB3); shared token fixtures for both stacks when automated tests resume | PW `40-security/api/token-parity.spec.ts` |
| R-12 | A payment could wait for the network's answer indefinitely: no age alert, no controlled manual resolution | Medium (operations) | **Fixed**: past `Payments:Saga:UnconfirmedAlertMinutes` readiness degrades and the Processing Monitor flags it; operations may then resolve it with four eyes (proposal with evidence by one person, approval by another, recent MFA, `payment.reconcile`); never released on a time-out; each step on the timeline and in the audit trail - `PaymentSaga.cs` | UT `Payments.Api.Tests` / `PaymentSaga_NetworkResolution_Edge_FourEyes`; PW `35-resilience/payments/payment-network-resilience.spec.ts` (proposal approved / rejected) |
| R-13 | A stale MFA sign-in was fixed only by signing out and in again (no OIDC step-up) | Low (usability) | **Fixed**: on 403 `mfa_required` the screen asks the Shell, which shows an in-page overlay framing the screen's BFF (`/api/auth/step-up`: `prompt=login`, `acr_values=mfa`; only the IDP's step-up code page may be framed, by the Shell only; "use a separate window" as the fallback); the IDP asks the signed-in person only for a fresh code, the BFF's tokens get a new `auth_time`, and the action is retried once - no sign-out. Payments, Accounts, Compliance, KYC and Operations | PW `40-security/mfa/step-up-without-sign-out.spec.ts` |
| R-14 | Rate limits (domain APIs, Journey APIs, IDP sign-in) and the SignalR connection cap count in memory, per instance: with several instances a caller gets a multiple of the budget, and a restart resets it | Medium in production | Accepted for PoC (single instance): production enforces deployment-wide limits at a gateway or with a shared store; the per-person keys stay. Raised by Microsoft EWP-07 and xAI F3-01 (round 3) | - |
| R-15 | Partner Products: EWP's customer record holds no date of birth, so the agent types it from Form VII and confirms it against photo ID; the Partner Products API trusts the customer facts the Journey API read for the agent (it does not call Customer Onboarding itself) | Medium | Accepted for now: the date of birth joins the customer record (onboarding) later. (Audit events: closed 2026-10-20 - every Form VII and purchase change reaches the audit trail through the Outbox, Integration-Event-Catalogue §4.8.) | - |
| R-16 | Partner Products: Temporal runs as the local development server (no TLS, no authorization on its frontend) | Low (PoC) / High in production for Temporal's frontend | Accepted for PoC (the polling half closed 2026-10-20: the worker listens to `payments.payment.events` and signals the workflow, polling every 5 minutes only as the safety net): production runs Temporal with mTLS and namespace authorization; workflow input already carries no token or personal data. Network ACLs alone are not enough (Temporal has no caller authentication of its own: whoever reaches the frontend can start, signal, terminate and read workflows), so production takes mTLS from the platform - the bank's service mesh or Temporal Cloud (mTLS or API keys) - plus an authorizer limiting each client to its namespace and actions; the frontend is reachable only from the Worker and the Partner Products Journey API. Not built in the PoC (decision 2026-10-20): the development server has no TLS, and an app-built TLS gateway would not reflect a bank's landscape | Manual runs T1, T2 (worker restart, missed event; 21-OCT) |

## 8. Assumptions and out of scope

- **Local development deployment.** One machine, `*.dev.localhost` host names, development certificates; PostgreSQL and Kafka on localhost.
- **External systems are simulators.** Their own security is out of scope; the platform's controls towards them (TB6) are in scope.
- **Database scripts are destructive bootstrap scripts** for development (no migrations); production would use versioned migrations with approval and backups.
- **No customer-facing channel yet.** All users are staff; a customer channel would add its own threats (credential stuffing at scale, mule accounts) to TB1.