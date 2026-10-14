# EWP V3 — Response to the AI architect re-reviews of 13-OCT-2026

Response date: 14-OCT-2026 · Review baseline: `EnterpriseWebPlatform_V3_20261013_160122`

## In short

Three reviews came back. This document answers every finding they raised:

| Review | File | Architecture | Security |
| --- | --- | --- | --- |
| Microsoft | `One-Microsoft-Way-Architect-Verdict.md` | 8.5 | 7.5 |
| xAI | `Bastrop-Texas-Architect-Verdict.md` | 8.5 | 8.5 |
| Google | `1600-Amphitheater-Architect-Verdict.md` | 7 | 7 |

The Microsoft and xAI findings were specific and grounded in the code. All of them are now **fixed**, apart from a few that can only be closed by production infrastructure; those are recorded as **production requirements** in `doc/Threat-Model.md` §7. The Google findings were mostly generic. Each one was checked against the code: most describe controls that already existed at the review baseline, so they are **rejected with evidence**, and two are partly accepted.

| Outcome | Count |
| --- | --- |
| Fixed after the review | 9 |
| Fixed before the review (already in the baseline) | 1 |
| Production requirement (cannot be closed in a PoC; recorded in the Threat Model) | 3 |
| Partly accepted (the core is mitigated or rejected; the remainder is a production requirement) | 2 |
| Rejected with evidence | 5 |

All fixes were made on 13–14 OCT 2026 in these commits:

| Commit | What it contains |
| --- | --- |
| `2c635d7` | Quick fixes: deny by default through one helper, fail-closed Operations configuration, Journey API rate limits and Problem Details, external-endpoint checks, Audit health, Threat-Model parity checklist |
| `276ce97` | Payment reconciliation with four eyes (R-12) |
| `ece52ca` | Step-up MFA without signing out (R-13) |
| `97ec907` | Documentation brought up to date |
| `6a9bc5a` … `6b7d466` | `:: ARCHITECTURE ::` / `:: SECURITY ::` comments at the place each control is enforced (search with `rg ":: (ARCHITECTURE\|SECURITY) ::"`) |

None of the 20 findings needs further work in the PoC. The open items are the production requirements below.

---

## Microsoft review

### EWP-01 — Operations MFA can fail open through configuration — **Fixed** (`2c635d7`)

- **Required environment.** `OPERATIONS_JOURNEY_ENVIRONMENT` must be set; it has no default.
- **Start-up guard.** `assertSafeOutsideDevelopment` in `src/Microservices/Operations/JourneyApi/src/configuration/journey-options.ts` stops the Operations Journey API from starting in two cases:
  - outside Development with `OPERATIONS_MFA_ENABLED` not `true`;
  - outside Development and Test with the simulator switch on.
- **Strict values.** Switches must be exactly `true` or `false`, so a misspelt value stops the start-up instead of silently becoming `false`. The step-up age must be a whole number from 1 to 60.
- **Agreement with the IDP.** `runnow.bat` documents that the value must match the IDP's `Mfa:Enabled`.

### EWP-02 — Audit integrity depends on protecting the signing key and checkpoint store — **Production requirement** (partly fixed in `2c635d7`)

- **Fixed now: monitoring.** Readiness on `src/Microservices/Audit/API/Infrastructure/AuditChainMonitor.cs` degrades in three cases, not only for an invalid signature:
  - verification is overdue;
  - the newest entry is not anchored;
  - checkpoint files go missing.
- **Already in the baseline: verification.** Checkpoints are signed (ECDSA P-256), and every verification checks them, including truncation of the trail (`AuditChainVerifier.cs`).
- **Production requirement (Threat-Model R-3).**
  - The signing key moves to a key vault or HSM.
  - The checkpoints move to immutable storage (WORM / object lock) administered by someone other than the database administrator.
  - This is infrastructure, not application code.

### EWP-03 — The Operations token-exchange chain needs production fail-closed validation — **Fixed** (`2c635d7`)

- **Configuration.** Every security setting of both Journey APIs is required, with no defaults: audience, caller client, environment and MFA policy (see EWP-01).
- **Delegated-token guard.** `delegated-token.guard.ts` requires a person (`sub`), the endpoint's scope, the web BFF as acting client (`act`), the endpoint's permission and, where marked, recent MFA. An endpoint with no access rule is denied.
- **Exchange allow-list.** The IDP's allow-list stays the authoritative restriction (`TokenExchangeGrantValidator.cs`).
- **Parity checklist.** The checklist for the .NET and NestJS token rules is in `doc/Threat-Model.md` §4 (TB3).

### EWP-04 — Parked-message recovery must be auditable and safe under replay — **Fixed before the review** (`53a1268`); verified

`ResendParkedMessage.cs` accepts only a message that is actually parked; one the relay is still trying, or has already sent, is refused with a conflict. A resend:

- keeps the **same message ID**;
- requires `workflow.reprocess`, recent MFA and a reason of 10–500 characters;
- is recorded on the saga timeline (`MESSAGE_RESENT`) with the person.

A replay cannot create a second financial effect, for three reasons:

- every consumer records the message ID in its Inbox;
- Accounts keeps one funds hold per payment;
- the saga accepts a reply only when its `CausationId` matches the current command (`PaymentSaga.cs`).

The Processing Monitor's "Parked messages" card shows each message's attempts and how often it was resent before.

### EWP-05 — Operations API needs abuse controls for privileged actions — **Fixed** (`2c635d7`)

`src/Microservices/Operations/JourneyApi/src/http/rate-limit.ts` has three limits:

| Limit | Applies to | Allowance |
| --- | --- | --- |
| Overall | All requests | 600 per minute |
| Reads | Each person and calling client | 120 per minute |
| Changes | Each person and calling client | 10 per minute |

A request over a limit gets HTTP 429 with `Retry-After`. Every simulator change is logged with the person's LAN ID and reverts to Healthy automatically after 15 minutes.

### EWP-06 — Payment saga needs a production-grade reconciliation contract — **Fixed** (`276ce97`)

- **Alert.** Past `Payments:Saga:UnconfirmedAlertMinutes` (production 60), readiness degrades and the Processing Monitor shows the payment as "Unconfirmed by network". The saga raises `PaymentUnconfirmedByNetwork` once per wait.
- **Manual resolution with four eyes.** Only then can operations resolve the payment by hand:
  - one person, holding `payment.reconcile` with recent MFA, proposes SENT (a network reference is required) or NOT_SENT, with evidence;
  - another person, never the proposer and never the payment's initiator, approves or rejects it.
- **No release on time-out.** A time-out alone never releases funds. The status enquiry continues, and the network's own answer supersedes any proposal.
- **Audit.** Every step is on the timeline and in the audit trail, with the person who took it.

### Production checklist items 5–6 (end-to-end authorization review, build and runtime checks)

- **Build.** Every batch of work is built and type-checked.
- **Runtime checks.** Playwright journeys cover onboarding, payments, resilience and security, including the MFA step-up test.
- **Formal review.** A deployment-level authorization review of the new Next.js BFF → NestJS Journey API → domain API paths is a production gate, recorded with Threat-Model R-11.

---

## xAI review

### F-1 — Customer KYC API has no global deny-by-default — **Fixed** (`2c635d7`)

All 8 domain APIs now map their controllers through one helper, `MapEwpApiControllers()`, with the `DenyAnonymousByDefault()` fallback policy (`src/Common/WebUtilities/Security/ApiEndpointAuthorization.cs`). A new controller without `[Authorize]` is therefore denied.

### F-2 — Dual auth stacks (.NET and NestJS) can drift — **Mitigated; shared fixtures are a production requirement**

- **Parity checklist.** `doc/Threat-Model.md` §4 (TB3) lists every token rule and where each stack enforces it: issuer, audience, `at+jwt`, `sub`, scope, `act`, permission, `amr` + `auth_time`.
- **Visible in code.** The `:: SECURITY ::` comments mark the same rules in both stacks.
- **Still to do.** Shared token fixtures that both stacks must pass are planned for when automated tests resume (Threat-Model R-11).

### F-3 — MFA step-up forces a full sign-out — **Fixed** (`ece52ca`)

- **Code-only window.** When an API answers 403 `mfa_required`, the screen opens a small window on its own BFF (`/api/auth/step-up`: `prompt=login`, `acr_values=mfa`). The IDP sees the existing session and asks only for a fresh authenticator code, with no password.
- **Retry.** The window returns to the fixed `/api/auth/step-up-done`; the screen closes it and retries the action once.
- **Coverage.** This is implemented in the Payments, Accounts, Compliance, KYC and Operations front ends.
- **Test.** It is verified by `tst/playwright/tests/40-security/mfa/step-up-without-sign-out.spec.ts`.

### F-4 — Production key custody for document KEKs and audit anchors — **Production requirement**

The cryptography is in place: envelope encryption with AES-256-GCM, and signed checkpoints. Key custody is infrastructure work:

- the document key-encryption keys move to a key vault or HSM, with a rotation runbook (R-2);
- the audit signing key also moves to a key vault or HSM, and the checkpoints to WORM storage under a separate administrator (R-3).

### F-5 — Inconsistent global authorization style — **Fixed** (`2c635d7`)

This uses the same helper as F-1. All domain APIs now follow one pattern: the authenticated fallback plus the API's scope policy.

### F-6 — External payment-network BaseUrl not host-constrained — **Fixed** (`2c635d7`)

`ExternalEndpoint` checks the payment network, core banking and the screening provider at start-up. Outside Development, it requires:

- HTTPS;
- a host from `PermittedHosts`;
- no loopback, private or link-local address.

Their HTTP clients follow no redirects, so a permitted host cannot bounce a call elsewhere.

### F-7 — Audit actor identity is envelope-trusted (not signed) — **Production requirement** (accepted for PoC, R-6)

Today, producers are trusted through Kafka SASL/SCRAM and per-topic ACLs. For production audit strength, each producer would sign its integration events with its own key (at least the actor and the message ID), and Audit would verify the signature before appending.

---

## Google review

These findings name generic areas rather than files or lines. Each was checked against the code at the review baseline.

### ARCH-01 — No idempotency in saga step retries / Kafka consumers — **Rejected**

Idempotent consumers exist throughout:

- **Domain APIs.** Each records the consumed message ID in `inbox_messages` in the **same transaction** as the state change:
  - Customer Onboarding, KYC, Compliance and Accounts;
  - Payments, Documents Management and Notifications.
  - See each context's `DbContext`.
- **Audit.** Audit skips a message ID it has already appended, under an advisory lock (`AuditTrailAppender.cs`).
- **Business keys.** Unique keys are a second guard: one case per application, one funds hold per payment, and one payment per Idempotency-Key.
- **Saga replies.** The saga also rejects stale replies through `CausationId`.

### ARCH-02 — Sagas can stall without timeout compensation — **Partly accepted**

- **Rejected: automatic compensation on time-out.** This is rejected by design. In banking, a time-out is not evidence: compensating a payment the network may already have accepted, or rejecting a customer because an officer has not yet acted, would be wrong.
- **Already in place: no silent stalls.**
  - **Payments.** Saga steps run under a lease; an unknown outcome triggers a status enquiry with back-off, then an age alert and four-eyes reconciliation (EWP-06).
  - **Messages.** Undeliverable messages are parked and can be resent.
  - **Subscribers.** These retry in place and dead-letter only bad messages.
  - **Compliance screening.** A backlog degrades readiness.
- **Production requirement: onboarding age alerts.** The onboarding steps wait mostly on people (KYC and compliance officers), so the remaining item is age monitoring and alerting for onboarding applications. It is a monitoring rule, not a compensation trigger.

### BP-01 — Anaemic domain models in the NestJS Journey APIs — **Rejected**

- **By design.** The Journey APIs hold no business rules: under the customer pattern, a Journey API composes calls to domain APIs.
- **Where the rules live.** Business invariants are enforced in the domain APIs' aggregates: `KycCase`, `ComplianceCase`, `PaymentSaga`, `Payment`, `Account`, `Customer`, `OnboardingApplication`, `Document`.
- **The Journey APIs' own logic.** What they implement themselves is authorization (the delegated-token guard), rate limits, error translation and orchestration of reads.
- **Reference.** See the Features document §3.8 (Anaemic domain model).

### BP-02 — No global Problem Details — **Rejected for .NET; the NestJS part was fixed** (`2c635d7`)

- **.NET.** Every domain API uses `AddProblemDetails()` and an `ApiExceptionHandler` that returns RFC 9457 Problem Details (which supersedes RFC 7807), with a `traceId` and no internal detail.
- **NestJS.** Both Journey APIs now use a `ProblemDetailsFilter`, and the BFFs read `detail` and `code`.

### SEC-01 — Insufficient audience validation in token exchange — **Rejected**

- **.NET APIs.** All 8 domain APIs validate issuer, audience, signature, lifetime and the `at+jwt` type.
- **Journey APIs.** Both pass `audience` to `jwtVerify`, and require the expected acting client.
- **IDP.** The IDP issues an exchanged token only:
  - for an allow-listed caller and target resource;
  - with that resource as the audience.
- **Result.** A token minted for one API is rejected by every other API.

### SEC-02 — Branch-level ABAC missing in Operations — **Rejected**

- **Operations has no customer data.** The Operations context shows platform health and the simulator switch only (`doc/Authorization-Model.md`); it never reads customer records.
- **Branch ABAC exists elsewhere.** It is enforced in the database query for every context that holds branch data:
  - customers, documents, KYC and compliance cases;
  - accounts and payments.
- **Branch from the token only.** The branch always comes from the token, never from a header.
- **Deliberate exception.** `operations_administrator` sees all branches, by design (a bank-wide operations role), and is still bound by separation of duties and four eyes.

### SEC-03 — No strict CSP in the Shell and SPAs — **Rejected**

- **.NET BFFs and the Shell.** These send a strict CSP that allows scripts and styles only by SHA-256 hash, with no `'unsafe-inline'`.
- **Next.js light BFFs.** These use a per-request nonce with `'strict-dynamic'`.
- **Every front end.** Each restricts `frame-ancestors` to the Shell (and the IDP during silent sign-in) and sends `nosniff`.
- **Uploads.** These are checked by content (magic bytes), stored with the verified type and served as attachments, except a verified PDF.

---

## What remains for a production go-ahead

These are the production requirements above, all recorded in `doc/Threat-Model.md` §7:

1. **Key custody.** Document key-encryption keys and the audit signing key move to a key vault or HSM; audit checkpoints move to WORM storage under a separate administrator (R-2, R-3).
2. **Signed events.** Integration events are signed for audit-grade actor attribution (R-6).
3. **Shared token fixtures.** Token fixtures run against both the .NET and NestJS stacks (R-11).
4. **Onboarding age alerts.** Age monitoring and alerting for onboarding applications (Google ARCH-02).
5. **Deployment controls.** mTLS between services, secrets from a secret store, and network controls for health and metrics endpoints (R-8, R-9, R-10).