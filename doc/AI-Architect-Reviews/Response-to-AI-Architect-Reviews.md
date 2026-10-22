# EWP V3 — Response to the AI architect reviews (rounds 1 to 4)

Response dates: 14-OCT-2026 (rounds 1–3) and 21-OCT-2026 (round 4).

## Round 4 — the brief given to the reviewers

The answers to round 4 are in the section [Round 4 — 21-OCT-2026](#round-4--21-oct-2026) below.

Round 4 covers **two repositories**:

- **EWP V3** (this repository): the bank's staff platform.
- **EWP V3 Partner Bank** (`EnterpriseWebPlatform_V3_PartnerBank`): a second, independent bank whose Digital Gold product EWP distributes at its branch counters.

The rest of this document answers rounds 1–3. Please do not raise those findings again unless the code now contradicts the answer given.

### Read these first

1. This section.
2. [Checklists](../Checklists/README.md), especially [15 — Workflow workers (Temporal)](../Checklists/15-Workflow-Workers-Temporal.md) and [12 — Kafka broker and ACLs](../Checklists/12-Kafka-Broker-and-ACLs.md).
3. [Threat-Model.md](../Threat-Model.md): §7 residual risks, especially R-10 and R-14 to R-16.
4. Partner Bank repository: `doc/Architecture.md` (decisions D1–D15) and `doc/Threat-Model.md` (T1–T28, residual risks R1–R10).
5. [Partner Products requirements](../Requirements/PartnerProducts-Requirements.md), and the design conversations [Partner-Bank-mTLS-PAR-And-Signed-Instructions.md](../Arch-Design-Visualizations/Partner-Bank-mTLS-PAR-And-Signed-Instructions.md) and [Temporal-Digital-Gold-Purchase.md](../Arch-Design-Visualizations/Temporal-Digital-Gold-Purchase.md).

### What changed since round 3 (14-OCT-2026)

| Area | Change | Commits |
| --- | --- | --- |
| Partner Products context (new) | Digital Gold at the counter. The stack is a Next.js SPA with a light BFF, a NestJS Journey API, and a .NET Domain API with PostgreSQL. The Domain API is also EWP's **adapter** to the Partner Bank: mutual TLS, a certificate-bound token, signed instructions. | `2c6d1136`, `bbaa5177`, `403403d6` |
| Digital Gold purchase | A signed purchase instruction, then a payment through the Payments saga, made as the staff member. A Temporal workflow takes the purchase to ALLOCATED or CANCELLED, run by a new worker with its own machine identity. | `dccf4aa6` |
| Purchase events (A1, A2) | Form VII and purchase changes go through a Transactional Outbox to `partner-products.digital-gold.events`, and from there into the audit trail. Payment outcomes wake the workflow with a data-less signal; polling every 5 minutes remains as the safety net. | `ff2f4b03`, `e3f6afb3` |
| "View at the Partner Bank" | A read-only staff page at the Partner Bank, opened in a new tab through a one-time handoff (see pattern C below). | `1bb69891` |
| Step-up without a pop-up | The authenticator code appears in an in-page overlay in the Shell. The IDP lets **only** the Shell frame **only** the step-up code page. | `1f6b3bf5` |
| Kafka over TLS (B8) | SASL_SSL. Every client checks the broker's certificate and host name before it signs in with SCRAM. | `8a6271e1` |
| Configuration per deployable (A4) | The shared `Common.Landscape` library is gone. Service URLs and client IDs come from each deployable's configuration and are checked at start-up. The IDP's `ClientIds` / `ClientBaseUrls` are the reference values. | `2344b75a` |
| Also | Resend of parked Outbox messages for Accounts and Compliance; Shell session fixes; Node dependency audit clean; Playwright journeys (including Digital Gold). | `1319c490`, `497b10af`, `1ab6a596`, `a4d6a06b` |

### Three ways EWP deals with the Partner Bank

Please check each against its own purpose. Measuring all three against FAPI 2.0 for a customer-facing channel would produce false findings.

**A. The customer's own sign-in at the Partner Bank: full PAR + JAR + RAR.**

- The Partner Bank's authorization server (Duende 8) requires the following:
  - **PAR (RFC 9126)**, on a separate mutual-TLS port;
  - a **signed request object (JAR, RFC 9101)**, ES256, with EWP's request-signing key;
  - **RAR (RFC 9396)** `authorization_details` with three types;
  - **PKCE** and `prompt=login`;
  - a consent page that shows the signed details;
  - access tokens bound to EWP's certificate (**RFC 8705**, `cnf`).
- The Partner Bank repository's `tools/HandshakeCheck` and `tools/JourneyCheck` exercise this end to end.
- **EWP has no customer-facing channel that uses it yet.** That is the planned A7 + B1 (customer self-service, FAPI 2.0, including DPoP or certificate-bound tokens throughout). Its absence in EWP is known, not an oversight.

**B. The counter (the distributor model): mutual TLS + EWP's own bound token + a signed instruction. No PAR.**

- The customer is in the branch and signs a paper Form VII (account opening) or a transfer slip (purchase). They do not sign in anywhere.
- The Partner Products API authenticates **EWP as an organisation**:
  - mutual TLS with EWP's client certificate;
  - a `client_credentials` token with scope `partner.distribution`, bound to that certificate;
  - each instruction as an EWP-signed JWT (`typ partner-instruction+jwt`, ES256, `aud partner-api`, 5 minutes, unique `jti`, which the Partner Bank accepts once).
- The staff member is the **actor** recorded on EWP's side (delegated token, branch, permission), not a party the Partner Bank authenticates.
- The Partner Bank relies on EWP to have checked the customer and their signature (Partner Bank R9). That reliance is the distribution agreement, not a missing control.

**C. "View at the Partner Bank": a PAR-style handoff, not RFC 9126 at the authorization server.**

1. EWP pushes a signed `staff_account_view` instruction over mutual TLS.
2. The Partner Bank returns a one-time `request_uri`, valid for 60 seconds.
3. The staff member's browser opens the Partner Bank's read-only page in a new tab with no opener. The request is redeemed once, and only its SHA-256 is kept.

There is no SSO federation between the banks: the Partner Bank trusts EWP's signed statement of who is looking (Partner Bank R10). Federation is described in the design conversation as the alternative.

### Known and out of scope for this round

- **Planned:**
  - A7 + B1: the customer channel and FAPI 2.0;
  - the D-series: containers, Terraform, GitHub Actions, CI gates, dashboards, gateway rate limits.
- **Accepted for the PoC, recorded with their production answer:**
  - R-10: development secrets in configuration;
  - R-14: rate limits counted per instance;
  - R-15: date of birth typed from Form VII;
  - R-16: Temporal development server without TLS or an authorizer (production: service mesh or Temporal Cloud mTLS, plus an authorizer);
  - the Kafka controller listener: PLAINTEXT, bound to localhost;
  - Partner Bank R1–R10: no certificate revocation, keys in files, and others.
- **Unit tests** cover Payments and Accounts only. The other contexts are covered by Playwright journeys.

### Where findings are most useful

- **Partner Products authorization:**
  - delegation pins (only the Journey API, for a person with the permission and branch);
  - the worker's pinned machine identity on internal endpoints;
  - branch scoping of every list and record.
- **Signed instructions:** replay (`jti`, instruction id fixed at the first send), what happens when an answer is lost (the record stays SENT; resending uses the same id), and signing-key separation (TLS key ≠ signing key).
- **The purchase workflow:**
  - exactly one ending per purchase;
  - idempotent activities;
  - a forged or repeated `paymentSettled` signal (it carries no data; the workflow re-reads the payment);
  - a worker restart mid-workflow.
- **The step-up overlay:** the single framing exception on the IDP (`SecurityHeadersAttribute`), and what a forged "done" message could gain.
- **A4:**
  - any remaining compiled-in address or client ID;
  - any start-up path that would still run with a missing pin.
- **B8:** any Kafka client that still connects without TLS or without checking the host name.

### How to file round 4

Please add one file per reviewer as `2026-10-2x-<Reviewer>-Architect-Verdict.md` in this folder. Give every finding:

- an ID;
- file and line evidence;
- a severity;
- whether it is a defect, a production requirement or a suggestion.

The answers will follow in this document under "Round 4".

## In short

Three AI architects (Microsoft, Google and xAI) reviewed EWP V3 four times. This document answers every finding of all four rounds, with the outcome and the commit or code that backs it.

- **Round 1 (received 12-OCT-2026).**
  - The Microsoft and xAI findings were concrete. Microsoft's included real defects: a stale saga reply could be accepted, and payment initiation trusted the screen's branch filter.
  - All of those were fixed within two days, except items that only production infrastructure can close.
  - Most of Google's findings describe code that does not exist in this repository (a hard-coded MFA bypass, direct Kafka publishing, synchronous audit calls). They are rejected with evidence.
- **Round 2 (13-OCT-2026), on the patched code.**
  - Microsoft and xAI confirmed the round-1 fixes in code and raised a smaller set of findings. Those are fixed too, apart from production requirements.
  - Google's findings were again mostly generic and are answered with evidence.
- **Round 3 (14-OCT-2026), with the response document and the checklists as starting points.**
  - All three confirmed the earlier fixes in code; none found a checklist `[x]` item that is false.
  - Microsoft and xAI found the same one new issue: rate limits are counted per instance. That is now a recorded production requirement (R-14).
  - Google's code claims were checked one by one. One was right: the subscribers' internal endpoints were exempt from rate limiting. That is fixed. Another was partly right (two simultaneous deliveries of one Payments reply); that is fixed too. The rest are rejected with evidence.
- **Round 4 (21-OCT-2026), across both repositories (EWP V3 and the Partner Bank).**
  - None of the three reviewers raised a finding. Each confirmed that the three Partner Bank interaction patterns match their stated purposes, and that the residual risks are as documented.
  - Their claims were checked against the code; the files and lines they cite exist and say what they report.
  - Microsoft added advice on what would take the scores to 9 or above: proving that the controls hold when things fail. Its suggestions are mapped below to the planned work.

### The review files

| Reviewer | Round 1 — 12-OCT-2026 | Round 2 — 13-OCT-2026 | Round 3 — 14-OCT-2026 | Round 4 — 21-OCT-2026 |
| --- | --- | --- | --- | --- |
| Microsoft | `2026-10-12-One-Microsoft-Way-Architect-Verdict.md` | `2026-10-13-One-Microsoft-Way-Architect-Verdict.md` | `2026-10-14-One-Microsoft-Way-Architect-Verdict.md` | `2026-10-21-One-Microsoft-Way-Architect-Verdict.md` |
| Google | `2026-10-12-1600-Amphitheater-Architect-Verdict.md` | `2026-10-13-1600-Amphitheater-Architect-Verdict.md` | `2026-10-14-1600-Amphitheater-Architect-Verdict.md` | `2026-10-21-1600-Amphitheater-Architect-Verdict.md` |
| xAI | `2026-10-12-Bastrop-Texas-Architect-Verdict.md` | `2026-10-13-Bastrop-Texas-Architect-Verdict.md` | `2026-10-14-Bastrop-Texas-Architect-Verdict.md` | `2026-10-21-Bastrop-Texas-Architect-Verdict.md` |

All twelve files are kept exactly as received; only their names carry the date. Round 1 is dated by the day it was received and added to the repository (`bf49510`); Microsoft's own text dates its review 10-OCT-2026.

The earliest review, `2026-10-02-Claude-Architect-Review.md` (Claude, 02-OCT-2026, on the code before `7a4e4b8`), is the "previous review" that xAI's round 1 refers to. It is not answered again here: it carries its own remediation table, and xAI's round 1 confirmed its fixes (Outbox relay, Documents Management authorization, stored XSS, secrets in shared libraries, refresh-token reuse, `at+jwt`).

### Ratings over the four rounds

| Reviewer | Round 1: Arch. | Round 1: Sec. | Round 2: Arch. | Round 2: Sec. | Round 3: Arch. | Round 3: Sec. | Round 4: Arch. | Round 4: Sec. |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Microsoft | 8 | 6.5 (production readiness 5.5) | 8.5 | 7.5 | 8.5 | 8 | **8.7** | **8.5** |
| xAI | not scored ("one of the stronger architectural PoCs") | not scored | 8.5 | 8.5 | 8.7 | 8.5 | **9.0** | **8.8** |
| Google | not scored | not scored | 7 | 7 | 8.5 | 8 | **9** | **9** |

### Outcomes

| Outcome | Round 1 | Round 2 | Round 3 | Round 4 | Total |
| --- | --- | --- | --- | --- | --- |
| Fixed after the review | 19 | 9 | 2 | — | 30 |
| Already in place at the review (shown with evidence) | 3 | 1 | — | — | 4 |
| Production requirement (cannot be closed in a PoC; recorded in `doc/Threat-Model.md` §7) | 8 | 3 | 4 | — | 15 |
| Partly accepted | 3 | 2 | 1 | — | 6 |
| Accepted as a deliberate design or PoC choice | 3 | — | — | — | 3 |
| Clarified (the control existed; the review could not see its settings) | 1 | — | — | — | 1 |
| Rejected with evidence | 7 | 5 | 4 | — | 16 |
| **Findings answered** | **44** | **20** | **11** | **0** | **75** |

None of the findings needs further work in the PoC. The open items are the production requirements collected at the end of this document.

### The commits behind the fixes

| Commit | Date | What it contains |
| --- | --- | --- |
| `bf49510` | 12-OCT | Round-1 fixes R1–R8: <ul><li>Accounts holds funds only on an account of the initiator's branch</li><li>atomic MFA code claims, and enrolment failures count towards lockout</li><li>account numbers masked in logs</li><li>auditor permission only through a delegated token</li><li>Idempotency-Key bound to the payment details</li><li>MFA and approval limits fail closed</li><li>Shell origin check from the menu</li><li>saga replies matched by `CausationId`</li></ul> |
| `72bb9ea` | 12-OCT | Payment network call outside the database transaction (claim / call / record); `doc/Threat-Model.md` written |
| `180a15a` | 12-OCT | Insecure `docker-compose.yml` deleted |
| `1c9b715` | 12-OCT | KYC documents encrypted at rest (envelope AES-256-GCM) |
| `4f25edd` | 12-OCT | Audit chain anchored outside its database (signed checkpoints) |
| `36f5581` | 12-OCT | Step-up MFA requires a RECENT sign-in (`amr` + `auth_time`) |
| `b5bd2ec` | 12-OCT | Per-person cap on SignalR connections |
| `9b6efbf` | 12-OCT | Network status enquiry: an unknown payment outcome never releases funds blind |
| `53a1268` | 13-OCT | Operator resend of parked Outbox messages |
| `2c635d7` | 13-OCT | Round-2 quick fixes: <ul><li>deny by default through one helper</li><li>fail-closed Operations configuration</li><li>Journey API rate limits and Problem Details</li><li>external-endpoint checks</li><li>Audit health</li><li>Threat-Model parity checklist</li></ul> |
| `276ce97` | 13-OCT | Payment reconciliation with four eyes |
| `ece52ca` | 13-OCT | Step-up MFA without signing out |
| `97ec907` | 14-OCT | Documentation brought up to date |
| `6a9bc5a` … `6b7d466` | 14-OCT | `:: ARCHITECTURE ::` / `:: SECURITY ::` comments where each control is enforced (search with `rg ":: (ARCHITECTURE\|SECURITY) ::"`) |
| — | 14-OCT | `doc/Checklists` (one checklist per deployable tier); before round 3: anti-forgery cookies HttpOnly, no fallback origins in the light BFFs |
| — | 14-OCT | Round-3 fixes (the commit that adds the round-3 section below): internal endpoints rate-limited, Payments Inbox re-checked under the saga lock, Journey API rate-limit sweep, R-14 recorded |

---

## Round 1 — 12-OCT-2026

### Microsoft (round 1)

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| 1 | A stale saga reply can be accepted as the current command's reply | **Fixed** (`bf49510`, R8) | <ul><li>The reply's `CausationId` reaches the saga, through the subscriber and the API.</li><li>Reservation and settlement replies count only for the current command.</li><li>Release confirmations count from any attempt, so a stuck compensation still recovers.</li><li>`PaymentSaga.cs`</li></ul> |
| 2 | Payment initiation relies on the screen's branch-filtered account lookup | **Fixed** (`bf49510`, R1) | <ul><li>Funds commands carry the initiator's branch.</li><li>Accounts reserves only on an account of that branch, under the account's row lock; otherwise it refuses with `ACCOUNT_NOT_IN_BRANCH`.</li></ul> |
| 3 | The Shell trusts an iframe's origin from its first message | **Fixed** (`bf49510`, R7) | <ul><li>`BSS_MFE_READY` is accepted only from the origin of the MFE the Shell loaded from the menu's `baseURL`.</li><li>Context updates are accepted only from that origin.</li></ul> |
| 4 | MFA and step-up are off by default | **Fixed** (`bf49510`, R6; Operations in `2c635d7`) | <ul><li>The IDP and the step-up APIs refuse to start outside Development with `Mfa:Enabled` off (`MfaStepUp.RequireMfaOutsideDevelopment`).</li><li>The Operations Journey API does the same.</li></ul> |
| 5 | The step runner holds a transaction open during the network call | **Fixed** (`72bb9ea`) | <ul><li>A short transaction claims the step under a 60 s lease.</li><li>The call runs with no transaction open.</li><li>The outcome is recorded under a fresh lock and version check.</li><li>`RunDueSagaStep.cs`; Threat-Model R-1</li></ul> |
| 6 | Auditor-only access can be bypassed by combined permissions | **Fixed** (`bf49510`, R4) | <ul><li>`payment.history.view` counts only in a delegated token from the Audit Journey API, even when the person also holds another role.</li><li>`EffectivePermissionsOf`</li></ul> |
| 7 | Idempotency does not compare the repeated request | **Fixed** (`bf49510`, R5) | The same key with other payment details returns 409; a true retry returns the same payment. |
| 8 | MFA replay protection is open to concurrent races | **Fixed** (`bf49510`, R2) | <ul><li>Each TOTP time step and each recovery code is claimed with one conditional `UPDATE`.</li><li>The failure count and the lockout are updated in one `UPDATE`.</li></ul> |
| 9 | Full account numbers in logs | **Fixed** (`bf49510`, R3) | <ul><li>Logs show the BSB and the last four digits only (`Common/Observability/LogMasking`).</li><li>This covers Payments, Accounts, the funds subscriber and the simulators.</li></ul> |
| 10 | Approval configuration should fail closed | **Fixed** (`bf49510`, R6) | <ul><li>`Payments:ApprovalThreshold` is required.</li><li>Every approval limit must be an amount or the explicit `Unlimited`; a blank value is refused.</li></ul> |
| 11 | The hash chain has no independent trust anchor | **Fixed** (`4f25edd`); key custody is a production requirement | <ul><li>Signed ECDSA P-256 checkpoints are written outside the database and checked by every verification.</li><li>Production: a key vault or HSM, and WORM storage (R-3).</li></ul> |
| 12 | One global audit chain serialises all writes | **Production requirement** | <ul><li>Accepted as the PoC trade-off, as the reviewer advised: "benchmark it first".</li><li>Partitioned chains with signed checkpoints are the path if load demands it.</li></ul> |
| 13 | Audit evidence depends on producer and broker trust | **Production requirement** (R-6) | <ul><li>Each producer already has its own Kafka user with per-topic ACLs (`Setup-KafkaSecurity.ps1`).</li><li>Signed event envelopes are the production step.</li></ul> |
| 14 | MFA is checked as ever-used, not recent | **Fixed** (`36f5581`, then `ece52ca`) | <ul><li>Step-up requires `amr` mfa and `auth_time` within `Mfa:StepUpMaxAgeMinutes`.</li><li>Since round 2, it is refreshed with `acr_values=mfa` in a code-only window, without signing out.</li></ul> |
| 15 | TOTP enrolment failures miss the lockout path | **Fixed** (`bf49510`, R2) | Wrong codes during enrolment count towards the lockout, on top of the per-IP login limit (20 a minute). |
| 16 | Data Protection and signing keys are deployment-critical | **Production requirement** (partly in code) | <ul><li>In code: outside Development, the IDP refuses to start without a signing certificate (`SigningCredentialExtensions.cs`).</li><li>In code: Data Protection keys are persistent and certificate-protected, with a fixed application name.</li><li>Production: backup, rotation and key custody.</li></ul> |
| 17 | Outbox parking needs a formal recovery process | **Fixed for Payments** (`53a1268`) | <ul><li>A resend keeps the same message ID and needs `workflow.reprocess`, recent MFA and a reason.</li><li>It is recorded on the timeline.</li><li>Accounts and Compliance park messages but have no resend screen yet; that tool is a production requirement.</li></ul> |
| 18 | Delivery order is not a global business order | **Accepted (design rule)** | <ul><li>Agreed. Order is enforced by the domain state machine: expected states, `CausationId`, idempotency.</li><li>Finding 1's fix is exactly that.</li><li>Saga plan, design rules.</li></ul> |
| 19 | Payment idempotency depends on the provider's contract | **Fixed in the PoC** (`9b6efbf`, `276ce97`); the provider contract is a production requirement | <ul><li>A status enquiry by Idempotency-Key; an unknown outcome never releases funds.</li><li>Age alert and four-eyes reconciliation.</li><li>The real provider's retention window must be tested.</li></ul> |
| 20 | In-process background workers need explicit ownership | **Accepted (design choice)** | <ul><li>The workers stay in the API deployable for the PoC.</li><li>Locks (`SKIP LOCKED`, leases), health checks and idempotency make them safe across instances.</li><li>Splitting them out later needs no design change.</li></ul> |
| 21 | Database scripts are destructive resets, not migrations | **Accepted for PoC** | <ul><li>Deliberate: each database has one full script, and databases are recreated.</li><li>Versioned migrations are a production requirement.</li></ul> |

Other remarks in the same review:

- **SignalR resource use.** **Fixed** (`b5bd2ec`): at most `Notifications:MaxConnectionsPerPerson` connections per person. A message is at most 4 KB, and the connection closes when the token expires.
- **WebSocket requests without an `Origin` header.** **Accepted.** Token validation is the control for non-browser clients, and browsers always send `Origin`.
- **Notification privacy.** **Production requirement.** Retention and access rules for stored notifications.
- **Deep links.** **Already in place.** The Shell opens only paths that match a fixed pattern (`openablePath`), through a microservice in the person's own menu. That BFF checks the path again against its allow-list, and the API authorizes the record.
- **Credentials and deployment.** Development secrets are accepted for the PoC (R-10); every secret fails closed when missing. Dependency scanning: `ps/Scan-Dependencies.ps1`. Secret scanning in CI, `AllowedHosts` and proxy settings for the real topology are production requirements.
- **Tests.** **Partly.** Playwright journeys now cover onboarding, payments (approval, rejection, closed bank, CoP), resilience (network Refusing, Down, Slow), security headers, session revocation and MFA step-up. .NET unit tests are on hold by the owner's decision.

### xAI (round 1)

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| C1 | The local Docker stack is wide open | **Fixed** (`180a15a`) | <ul><li>`docker-compose.yml` is deleted.</li><li>Local Kafka runs SASL/SCRAM with a user per deployable and per-topic ACLs (`ps/kafka/Setup-KafkaSecurity.ps1`).</li></ul> |
| C2 | Development client secrets committed | **Accepted for PoC** (R-10) | <ul><li>Development values only.</li><li>Every secret is read from configuration, and a missing one stops start-up.</li><li>Production uses a secret store.</li></ul> |
| C3 | Documents not encrypted at rest | **Fixed** (`1c9b715`) | <ul><li>Envelope encryption with AES-256-GCM: a key per document, wrapped by a rotatable key-encryption key.</li><li>The document ID is bound in, and plaintext files are refused.</li><li>Key custody: R-2.</li></ul> |
| C4 | Metrics and health endpoints are anonymous | **Accepted for PoC** (R-8) | <ul><li>A deployment control: network policy plus a scrape token or mTLS.</li><li>The endpoints expose counts and status only, no business data.</li></ul> |
| C5 | No circuit breakers on outbound calls | **Already in place** | <ul><li>`AddStandardResilienceHandler` (timeout, retry with jitter, circuit breaker) was configured in 15 files at the review baseline.</li><li>That covers the subscribers, BFF proxies, M2M token clients and external systems.</li><li>xAI round 2 confirmed this.</li></ul> |
| C6 | Shared BFF state database | **Already in place** | <ul><li>Each BFF has its own schema and database role in `EwpBffStateDb`.</li><li>Data Protection keys are separated by application name and protected by a certificate.</li><li>xAI round 2 confirmed this.</li></ul> |
| H1 | Service-to-service traffic not forced to mTLS | **Production requirement** (R-9) | Service mesh. |
| H2 | Kafka ACLs not enforced locally | **Already in place** | <ul><li>`Setup-KafkaSecurity.ps1` (since `56c482e`, 08-OCT) creates the SCRAM users and ACLs.</li><li>Every client fails closed without SASL credentials.</li><li>Google round 2 verified this.</li></ul> |
| H3 | Authorization completeness uneven across contexts | **Partly** | <ul><li>The permission and role matrix is in `doc/Authorization-Model.md`.</li><li>Deny by default is in every API (`2c635d7`).</li><li>Automated matrix tests come when tests resume.</li></ul> |
| H4 | Audit chain has no external anchor | **Fixed** (`4f25edd`) | Same fix as Microsoft 11. |
| H5 | No threat model | **Fixed** (`72bb9ea`) | `doc/Threat-Model.md`: assets, trust boundaries TB1–TB6, STRIDE, and the residual-risk table, kept up to date with each fix. |
| H6 | Security test coverage unknown | **Partly** | <ul><li>Today: security headers, IDP session revoked after sign-out, MFA step-up.</li><li>When tests resume: CSRF, token-exchange abuse and content-type confusion.</li></ul> |
| H7 | Rate-limit settings opaque | **Clarified** | <ul><li>Domain APIs use sliding windows: 600 requests a minute per person, 60 changes a minute per person, 120 a minute per anonymous IP and 3,000 a minute per machine client.</li><li>The IDP allows 20 sign-ins a minute per IP, on top of per-account lockout.</li><li>Journey APIs: 120 reads and 10 changes a minute per person (`2c635d7`).</li><li>These are listed in each README's "Security controls" table.</li></ul> |
| H8 | Security events not structured for alerting | **Production requirement** | <ul><li>Refusals are logged with the trace ID.</li><li>Audit records every audit-trail read as an ACCESS entry.</li><li>A security-event stream and dashboards (sign-in failures, MFA lockouts, token-exchange refusals) belong to production monitoring.</li></ul> |

The review's "architectural observations" table:

- **DDD maturity.** Every .NET context now enforces its rules in aggregates: `Customer`, `OnboardingApplication`, `KycCase`, `ComplianceCase`, `Document`, `Account`, `Payment`, `PaymentSaga`.
- **The saga style mix.** Choreography for onboarding and orchestration for payments is intentional (Saga plan). Runbooks per style are a production item.
- **Simulators.** Their behaviour endpoints answer on loopback only. The Operations switch exists only in Development and Test and refuses to start otherwise (`2c635d7`).
- **Fragile CSP build.** The CSP fails closed: without the exported pages, no inline hash is allowed, so inline code is blocked rather than allowed (`ContentSecurityPolicy.cs`).
- **Security dashboards.** See H8.
- **Schema evolution.** See Microsoft 21.

### Google (round 1)

This review quotes code that is not in the repository. The checks below were run against the review baseline and the current tree.

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| 1 | Hard-coded MFA master codes ("999888", "000000") | **Rejected** | <ul><li>No such code exists (`rg 999888 src` finds nothing).</li><li>`MfaService.cs` verifies every code against the stored, Data-Protection-encrypted secret.</li><li>Codes are single use per time step, claimed atomically since R2, under lockout.</li></ul> |
| 2 | MFA state kept in readable, tamperable cookies | **Rejected** | <ul><li>The pending sign-in is encrypted and authenticated with ASP.NET Data Protection (`PendingSignIn.cs`).</li><li>It is a short-lived `__Host-` cookie, so a changed cookie fails to decrypt.</li><li>BFF sessions are server-side.</li></ul> |
| 3 | IDOR: `[Authorize(Roles = "Customer")]` without ownership checks | **Rejected** | <ul><li>There is no customer-facing role; every user is staff.</li><li>Each object is authorized by branch ABAC and managing-agent ReBAC (`CustomerResourceAuthorization.cs`), Payments branch scope, and the Accounts branch check under lock (R1).</li><li>An out-of-scope record answers 404.</li></ul> |
| 4 | No Outbox: dual write to the database and Kafka | **Rejected** | <ul><li>Every producing context writes its events to `outbox_messages` in the same transaction as the state change: Customer Onboarding, KYC, Compliance, Accounts and Payments.</li><li>A relay publishes them (`FOR UPDATE SKIP LOCKED`, per-aggregate order).</li></ul> |
| 5 | Consumers are not idempotent | **Rejected** | <ul><li>Every consumer records the message ID in `inbox_messages` in the same transaction.</li><li>Audit skips a known message ID.</li></ul> |
| 6 | No step time-outs or dead-letter handling in the payment saga | **Partly** | <ul><li>Dead-letter topics, step leases, back-off, the status enquiry and the age alert all exist.</li><li>The "locked transaction context" was real at the time (Microsoft 5) and fixed in `72bb9ea`.</li><li>Automatic compensation on a time-out is rejected by design; see round 2, ARCH-02.</li></ul> |
| 7 | SignalR without a backplane | **Production requirement** | <ul><li>Documented: one Notifications instance in the PoC.</li><li>A Redis or Azure SignalR backplane for more than one instance (Notifications README; Features 1.5.4 "Not yet").</li></ul> |
| 8 | Blocking I/O inside hub handlers | **Rejected** | <ul><li>The hub has no client-callable methods; on connect it only joins the groups derived from the token.</li><li>Notifications are built and stored before the push, and lists are read over REST.</li></ul> |
| 9 | Audit called synchronously over HTTP | **Rejected** | <ul><li>Audit consumes the business topics from Kafka (`AuditTrailSubscriber.cs`) and has no write endpoint.</li><li>No business flow calls it.</li></ul> |

---

## Round 2 — 13-OCT-2026

Review baseline: `EnterpriseWebPlatform_V3_20261013_160122`.

### Microsoft (round 2)

#### EWP-01 — Operations MFA can fail open through configuration — **Fixed** (`2c635d7`)

- **Required environment.** `OPERATIONS_JOURNEY_ENVIRONMENT` must be set; it has no default.
- **Start-up guard.** `assertSafeOutsideDevelopment` in `src/Microservices/Operations/JourneyApi/src/configuration/journey-options.ts` stops the Operations Journey API from starting in two cases:
  - outside Development with `OPERATIONS_MFA_ENABLED` not `true`;
  - outside Development and Test with the simulator switch on.
- **Strict values.** Switches must be exactly `true` or `false`, so a misspelt value stops the start-up instead of silently becoming `false`. The step-up age must be a whole number from 1 to 60.
- **Agreement with the IDP.** `runnow.bat` documents that the value must match the IDP's `Mfa:Enabled`.

#### EWP-02 — Audit integrity depends on protecting the signing key and checkpoint store — **Production requirement** (partly fixed in `2c635d7`)

- **Fixed now: monitoring.** Readiness on `src/Microservices/Audit/API/Infrastructure/AuditChainMonitor.cs` degrades in three cases, not only for an invalid signature:
  - verification is overdue;
  - the newest entry is not anchored;
  - checkpoint files go missing.
- **Already in the baseline: verification.** Checkpoints are signed (ECDSA P-256), and every verification checks them, including truncation of the trail (`AuditChainVerifier.cs`).
- **Production requirement (Threat-Model R-3).**
  - The signing key moves to a key vault or HSM.
  - The checkpoints move to immutable storage (WORM / object lock) administered by someone other than the database administrator.
  - This is infrastructure, not application code.

#### EWP-03 — The Operations token-exchange chain needs production fail-closed validation — **Fixed** (`2c635d7`)

- **Configuration.** Every security setting of both Journey APIs is required, with no defaults: audience, caller client, environment and MFA policy (see EWP-01).
- **Delegated-token guard.** `delegated-token.guard.ts` requires a person (`sub`), the endpoint's scope, the web BFF as acting client (`act`), the endpoint's permission and, where marked, recent MFA. An endpoint with no access rule is denied.
- **Exchange allow-list.** The IDP's allow-list stays the authoritative restriction (`TokenExchangeGrantValidator.cs`).
- **Parity checklist.** The checklist for the .NET and NestJS token rules is in `doc/Threat-Model.md` §4 (TB3).

#### EWP-04 — Parked-message recovery must be auditable and safe under replay — **Fixed before the review** (`53a1268`); verified

`ResendParkedMessage.cs` accepts only a message that is actually parked; one the relay is still trying, or has already sent, is refused with a conflict. A resend:

- keeps the **same message ID**;
- requires `workflow.reprocess`, recent MFA and a reason of 10–500 characters;
- is recorded on the saga timeline (`MESSAGE_RESENT`) with the person.

A replay cannot create a second financial effect, for three reasons:

- every consumer records the message ID in its Inbox;
- Accounts keeps one funds hold per payment;
- the saga accepts a reply only when its `CausationId` matches the current command (`PaymentSaga.cs`).

The Processing Monitor's "Parked messages" card shows each message's attempts and how often it was resent before.

#### EWP-05 — Operations API needs abuse controls for privileged actions — **Fixed** (`2c635d7`)

`src/Microservices/Operations/JourneyApi/src/http/rate-limit.ts` has three limits:

| Limit | Applies to | Allowance |
| --- | --- | --- |
| Overall | All requests | 600 per minute |
| Reads | Each person and calling client | 120 per minute |
| Changes | Each person and calling client | 10 per minute |

A request over a limit gets HTTP 429 with `Retry-After`. Every simulator change is logged with the person's LAN ID and reverts to Healthy automatically after 15 minutes.

#### EWP-06 — Payment saga needs a production-grade reconciliation contract — **Fixed** (`276ce97`)

- **Alert.** Past `Payments:Saga:UnconfirmedAlertMinutes` (production 60), readiness degrades and the Processing Monitor shows the payment as "Unconfirmed by network". The saga raises `PaymentUnconfirmedByNetwork` once per wait.
- **Manual resolution with four eyes.** Only then can operations resolve the payment by hand:
  - one person, holding `payment.reconcile` with recent MFA, proposes SENT (a network reference is required) or NOT_SENT, with evidence;
  - another person, never the proposer and never the payment's initiator, approves or rejects it.
- **No release on time-out.** A time-out alone never releases funds. The status enquiry continues, and the network's own answer supersedes any proposal.
- **Audit.** Every step is on the timeline and in the audit trail, with the person who took it.

#### Production checklist items 5–6 (end-to-end authorization review, build and runtime checks)

- **Build.** Every batch of work is built and type-checked.
- **Runtime checks.** Playwright journeys cover onboarding, payments, resilience and security, including the MFA step-up test.
- **Formal review.** A deployment-level authorization review of the new Next.js BFF → NestJS Journey API → domain API paths is a production gate, recorded with Threat-Model R-11.

---

### xAI (round 2)

#### F-1 — Customer KYC API has no global deny-by-default — **Fixed** (`2c635d7`)

All 8 domain APIs now map their controllers through one helper, `MapEwpApiControllers()`, with the `DenyAnonymousByDefault()` fallback policy (`src/Common/WebUtilities/Security/ApiEndpointAuthorization.cs`). A new controller without `[Authorize]` is therefore denied.

#### F-2 — Dual auth stacks (.NET and NestJS) can drift — **Mitigated; shared fixtures are a production requirement**

- **Parity checklist.** `doc/Threat-Model.md` §4 (TB3) lists every token rule and where each stack enforces it: issuer, audience, `at+jwt`, `sub`, scope, `act`, permission, `amr` + `auth_time`.
- **Visible in code.** The `:: SECURITY ::` comments mark the same rules in both stacks.
- **Still to do.** Shared token fixtures that both stacks must pass are planned for when automated tests resume (Threat-Model R-11).

#### F-3 — MFA step-up forces a full sign-out — **Fixed** (`ece52ca`)

- **Code-only window.** When an API answers 403 `mfa_required`, the screen opens a small window on its own BFF (`/api/auth/step-up`: `prompt=login`, `acr_values=mfa`). The IDP sees the existing session and asks only for a fresh authenticator code, with no password.
- **Retry.** The window returns to the fixed `/api/auth/step-up-done`; the screen closes it and retries the action once.
- **Coverage.** This is implemented in the Payments, Accounts, Compliance, KYC and Operations front ends.
- **Test.** It is verified by `tst/playwright/tests/40-security/mfa/step-up-without-sign-out.spec.ts`.

#### F-4 — Production key custody for document KEKs and audit anchors — **Production requirement**

The cryptography is in place: envelope encryption with AES-256-GCM, and signed checkpoints. Key custody is infrastructure work:

- the document key-encryption keys move to a key vault or HSM, with a rotation runbook (R-2);
- the audit signing key also moves to a key vault or HSM, and the checkpoints to WORM storage under a separate administrator (R-3).

#### F-5 — Inconsistent global authorization style — **Fixed** (`2c635d7`)

This uses the same helper as F-1. All domain APIs now follow one pattern: the authenticated fallback plus the API's scope policy.

#### F-6 — External payment-network BaseUrl not host-constrained — **Fixed** (`2c635d7`)

`ExternalEndpoint` checks the payment network, core banking and the screening provider at start-up. Outside Development, it requires:

- HTTPS;
- a host from `PermittedHosts`;
- no loopback, private or link-local address.

Their HTTP clients follow no redirects, so a permitted host cannot bounce a call elsewhere.

#### F-7 — Audit actor identity is envelope-trusted (not signed) — **Production requirement** (accepted for PoC, R-6)

Today, producers are trusted through Kafka SASL/SCRAM and per-topic ACLs. For production audit strength, each producer would sign its integration events with its own key (at least the actor and the message ID), and Audit would verify the signature before appending.

---

### Google (round 2)

These findings name generic areas rather than files or lines. Each was checked against the code at the review baseline.

#### ARCH-01 — No idempotency in saga step retries / Kafka consumers — **Rejected**

Idempotent consumers exist throughout:

- **Domain APIs.** Each records the consumed message ID in `inbox_messages` in the **same transaction** as the state change:
  - Customer Onboarding, KYC, Compliance and Accounts;
  - Payments, Documents Management and Notifications.
  - See each context's `DbContext`.
- **Audit.** Audit skips a message ID it has already appended, under an advisory lock (`AuditTrailAppender.cs`).
- **Business keys.** Unique keys are a second guard: one case per application, one funds hold per payment, and one payment per Idempotency-Key.
- **Saga replies.** The saga also rejects stale replies through `CausationId`.

#### ARCH-02 — Sagas can stall without timeout compensation — **Partly accepted**

- **Rejected: automatic compensation on time-out.** This is rejected by design. In banking, a time-out is not evidence: compensating a payment the network may already have accepted, or rejecting a customer because an officer has not yet acted, would be wrong.
- **Already in place: no silent stalls.**
  - **Payments.** Saga steps run under a lease; an unknown outcome triggers a status enquiry with back-off, then an age alert and four-eyes reconciliation (EWP-06).
  - **Messages.** Undeliverable messages are parked and can be resent.
  - **Subscribers.** These retry in place and dead-letter only bad messages.
  - **Compliance screening.** A backlog degrades readiness.
- **Done since (14-OCT): onboarding age alerts.** The onboarding steps wait mostly on people (KYC and compliance officers), so the remaining item was age monitoring - a monitoring rule, not a compensation trigger.
  - The Customer Onboarding API's readiness turns Degraded when an application waits too long in one step: a machine step after `Onboarding:Alerts:HandoverMinutes` (production 15), an officer step after `OfficerDecisionMinutes` (production one day), and `CompensationFailed` always.
  - It shows on Platform Health and on the `/metrics` health gauge (`OnboardingAgeHealthCheck.cs`).

#### BP-01 — Anaemic domain models in the NestJS Journey APIs — **Rejected**

- **By design.** The Journey APIs hold no business rules: under the customer pattern, a Journey API composes calls to domain APIs.
- **Where the rules live.** Business invariants are enforced in the domain APIs' aggregates: `KycCase`, `ComplianceCase`, `PaymentSaga`, `Payment`, `Account`, `Customer`, `OnboardingApplication`, `Document`.
- **The Journey APIs' own logic.** What they implement themselves is authorization (the delegated-token guard), rate limits, error translation and orchestration of reads.
- **Reference.** See the Features document §3.8 (Anaemic domain model).

#### BP-02 — No global Problem Details — **Rejected for .NET; the NestJS part was fixed** (`2c635d7`)

- **.NET.** Every domain API uses `AddProblemDetails()` and an `ApiExceptionHandler` that returns RFC 9457 Problem Details (which supersedes RFC 7807), with a `traceId` and no internal detail.
- **NestJS.** Both Journey APIs now use a `ProblemDetailsFilter`, and the BFFs read `detail` and `code`.

#### SEC-01 — Insufficient audience validation in token exchange — **Rejected**

- **.NET APIs.** All 8 domain APIs validate issuer, audience, signature, lifetime and the `at+jwt` type.
- **Journey APIs.** Both pass `audience` to `jwtVerify`, and require the expected acting client.
- **IDP.** The IDP issues an exchanged token only:
  - for an allow-listed caller and target resource;
  - with that resource as the audience.
- **Result.** A token minted for one API is rejected by every other API.

#### SEC-02 — Branch-level ABAC missing in Operations — **Rejected**

- **Operations has no customer data.** The Operations context shows platform health and the simulator switch only (`doc/Authorization-Model.md`); it never reads customer records.
- **Branch ABAC exists elsewhere.** It is enforced in the database query for every context that holds branch data:
  - customers, documents, KYC and compliance cases;
  - accounts and payments.
- **Branch from the token only.** The branch always comes from the token, never from a header.
- **Deliberate exception.** `operations_administrator` sees all branches, by design (a bank-wide operations role), and is still bound by separation of duties and four eyes.

#### SEC-03 — No strict CSP in the Shell and SPAs — **Rejected**

- **.NET BFFs and the Shell.** These send a strict CSP that allows scripts and styles only by SHA-256 hash, with no `'unsafe-inline'`.
- **Next.js light BFFs.** These use a per-request nonce with `'strict-dynamic'`.
- **Every front end.** Each restricts `frame-ancestors` to the Shell (and the IDP during silent sign-in) and sends `nosniff`.
- **Uploads.** These are checked by content (magic bytes), stored with the verified type and served as attachments, except a verified PDF.

---

## Round 3 — 14-OCT-2026

Review baseline: the code after the pre-review hardening. This time the reviewers were asked to start from this document, the checklists and Threat-Model §7, and to say whether each finding is new, reopens an earlier one, or disagrees with this document.

### Microsoft (round 3)

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| EWP-07 | Journey API rate limits are process-local, not deployment-wide | **Production requirement** (R-14) | <ul><li>Agreed, and it applies more widely: the domain APIs, the IDP's sign-in limit and the SignalR connection cap also count in memory, per instance.</li><li>Recorded as Threat-Model R-14.</li><li>The code comments, READMEs and checklists 01 / 08 / 09 now say "per instance".</li><li>Production enforces deployment-wide limits at a gateway or with a shared store; the per-person keys stay.</li></ul> |

Its three qualifications are already recorded open items, which the reviewer says it did not promote to findings:
- no resend tool for parked Accounts and Compliance messages;
- shared token fixtures (R-11);
- narrower per-table grants (checklist 14).

### xAI (round 3)

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| F3-01 | NestJS rate limits are process-local | **Production requirement** (R-14) | Same as Microsoft EWP-07. |
| F3-02 | The .NET and NestJS token checks must stay in lock-step | **Production requirement** (R-11) | Agreed: the parity checklist and the code comments are documentation, not executable parity. Shared token fixtures for both stacks come when automated tests resume. |
| F3-03 | Expired rate-limit windows are swept only at 10,000 entries | **Fixed** | Both Journey APIs now sweep expired windows at most once a minute, so memory follows the callers active in the last window (`src/http/rate-limit.ts`). |

### Google (round 3)

Each claim was checked against the code:

| # | Finding | Outcome | Evidence |
| --- | --- | --- | --- |
| EWP-302 (and §1) | Outbox publishers lack `FOR UPDATE SKIP LOCKED`, so duplicates are published | **Rejected** | <ul><li>`CustomerOutboxPublisher.cs` line 111 selects with `FOR UPDATE SKIP LOCKED`, as do the KYC, Compliance, Accounts and Payments relays.</li><li>A repeat would in any case carry the same message ID, which every consumer's Inbox absorbs.</li></ul> |
| EWP-301 (and §1) | Token exchange does not validate audience and scope at the domain API boundary | **Rejected** | <ul><li>Every domain API validates issuer, audience, signature, lifetime and `at+jwt`.</li><li>Both Journey APIs verify `audience` and `typ`, and require their web BFF as `act`.</li><li>The IDP exchanges only for allow-listed caller / resource pairs and puts the target resource in `aud`.</li><li>Same answer as round 2, SEC-01.</li></ul> |
| §2 | The `__Host-` prefix is omitted in some cookie overrides | **Rejected** | <ul><li>Every cookie name carries `__Host-`: `CookieNames.cs`, the anti-forgery cookies, the KYC BFF session, the light BFFs' `config.ts`, and the IDP's pending sign-in.</li><li>No configuration file overrides a cookie name.</li></ul> |
| §2 | Simultaneous redeliveries fail on the Inbox key instead of being absorbed | **Partly accepted; fixed** | <ul><li>Customer Onboarding, KYC, Compliance, Accounts, Documents Management and Notifications already catch the unique violation and answer "already processed".</li><li>Payments checked its Inbox only before taking the saga row lock. Two copies of one reply at the same moment made the second fail with a 500. It failed safe — nothing happened twice, and the retry was answered as already processed — but it was noisy.</li><li>`HandleSagaReply.cs` now checks the Inbox again under the lock.</li></ul> |
| §3 | Sender-constrained tokens (DPoP) needed before production | **Production requirement** (with R-9) | <ul><li>Browser tokens never leave the BFFs, so DPoP's main use (tokens in the browser) does not apply.</li><li>For service-to-service calls, certificate-bound tokens (RFC 8705) come with mTLS in production.</li></ul> |
| §4 | Internal endpoints bypass the global rate limiters | **Fixed** | <ul><li>Correct: `/internal` was on the limiter's always-exempt list.</li><li>The subscribers' internal endpoints are now limited by their machine client's budget (3,000 a minute) in all seven APIs that have them.</li><li>A 429 is transient for a subscriber (retry in place, Inbox-safe), so a backlog only slows down, while a stolen worker secret can no longer flood its API.</li><li>`RateLimiting.cs`, each `Program.cs`.</li></ul> |
| EWP-303 | Exception handlers log unmasked PII and account numbers | **Rejected** | <ul><li>`ApiExceptionHandler` logs the exception with the trace ID and returns no internals.</li><li>No domain exception message contains an account number.</li><li>EF Core sensitive-data logging and Npgsql's "Include Error Detail" are off, so database errors carry no row values.</li><li>Request bodies are never logged.</li></ul> |

Google's second, "nice-to-have" list is not counted as findings. Most of it describes technology this platform does not use:

| Suggestion | Answer |
| --- | --- |
| Adaptive outbox polling and batch sizing | Not needed at this scale: fixed batches of 50 with back-off. Revisit with load tests (production item 15). |
| DLQ depth metrics and alerting | Agreed; already a production item (checklist 11, and item 11 below). |
| Refresh-token rotation grace period | Not applicable: refresh tokens are one-time only, with no grace window, and stay in the BFF's server-side session. |
| BFF session invalidation across Redis replicas | Not applicable: sessions are server-side in PostgreSQL and read on every request; back-channel logout deletes them. |
| YARP gateway rate-limit partitioning | No gateway exists; the limits are per person, per machine client and per IP. The deployment-wide gap is R-14. |
| Generic 503 for non-idempotent mutations | Already handled: payments carry an Idempotency-Key and decisions are sent once (`postOnce`); an open circuit answers 503 before anything is sent. |
| Correlation IDs dropped on Kafka | Not the case: `traceparent`, workflow, correlation and causation IDs are copied into Kafka headers and continued by the subscribers. |
| Real-time audit hash verification | Each entry's hash is computed and chained inside the append transaction. Full verification and anchoring run periodically by design (R-3). |
| Nullable reference types, compiled queries, module federation | Nullable is enabled in every project. Compiled queries are an optional optimisation. The MFEs are composed by iframes, not module federation. |

---

## Round 4 — 21-OCT-2026

Review baseline: EWP V3 at `749b4ddf` (after B8 and A4) and the Partner Bank repository, both as of 21-OCT-2026. The reviewers were given the brief at the top of this document. In particular, they were asked not to raise items already recorded as known, accepted, planned or production requirements unless the code contradicts the documents.

**No reviewer raised a finding.** What each one reported as sound was checked against the code:

### Microsoft (round 4)

No new Critical, High or Medium defect. Microsoft calls this "a qualified result": it is a source-level review, with no protocol journeys run and no penetration test.

| Reported as sound | Checked |
| --- | --- |
| Pattern B: the signed-instruction validator pins the signing key, algorithm, `typ`, issuer, audience, lifetime and instruction ID | Partner Bank `src/PartnerBank.Api/Distribution/SignedInstructions.cs`. Each instruction is done once (`GoldDesk.cs`). An account opening is keyed on `InstructionId`, with a unique index in `PartnerDbContext.cs`. A purchase is keyed on `AuthorizationRef` (the instruction ID). The same instruction again returns the same account or order. |
| Pattern C: the one-time staff-view handoff | `StaffViews.cs`: a replayed instruction is refused (`usedInstructions.TryAdd`), the reference is random with a 60-second life, and redemption removes it atomically. |
| The workflow trusts the Payments API, not the signal | `workflows.ts`: the signal handler only sets a flag. Each pass re-reads the payment by its Idempotency-Key and allocates only on `COMPLETED`. |
| Worker Kafka client: SASL_SSL, SCRAM-SHA-512, host-name check; only payments to the settlement account wake a workflow | `payment-events.ts`. |

### xAI (round 4)

No finding that meets the bar. The 9.0 / 8.8 scores lose points only for items already accepted (R-16 Temporal hardening; Partner Bank R1–R10).

| Reported as sound | Checked |
| --- | --- |
| Pattern B end to end: ES256 `partner-instruction+jwt`, `aud partner-api`, 5 minutes, stable `jti`; server certificate checked against the PoC root only | `PartnerBankClient.cs` (signing, cached bound token); Partner Bank `SignedInstructions.cs`, `GoldDesk.cs`. |
| One ending per purchase; the signal carries no authority; 400 / 403 / 409 are not retried; the worker identity is pinned on internal endpoints | `workflows.ts`, `activities.ts` (`ApplicationFailure.nonRetryable`), `GoldPurchase.cs` (`MarkPaid`, `EnsureCancellable`), `DigitalGoldPolicies.cs`. |
| B8: every .NET Kafka client goes through `KafkaClientSecurity.Apply` (fails closed, host-name check); the Node worker does the same | `KafkaClientSecurity.cs`, `payment-events.ts`. |

One wording correction: xAI writes "jti once-only on both sides". It is once-only on the **Partner Bank's** side. EWP keeps the `jti` stable, so that a resend is recognised there as the same instruction.

### Google (round 4)

No finding; 9 / 9. The files it names exist (`TrustedBackchannel.cs`, `ClientCertificates.cs`, `PortGuard.cs`, `SecurityHeaders.cs`, `DistributionApi.cs`, `ExternalApi.cs`, `GoldApi.cs`). However, it gives no line-level evidence. "Patterns A, B and C cleanly implemented" also overstates EWP's side: EWP does not use pattern A yet (A7 + B1). This verdict therefore carries less weight than the other two.

### What it takes to reach 9 or above (Microsoft's advice)

Microsoft recommends no new patterns. Instead, it asks for **evidence that the controls hold when things fail**: an assurance suite of negative and failure-injection tests across service boundaries. Most of it maps onto work that was already planned:

| Microsoft's suggestion | Response | Planned as |
| --- | --- | --- |
| Temporal worker stops mid-activity; duplicate or late events; Kafka down | Agreed. The controls exist (data-less signal, 5-minute fallback, idempotent activities); the failure runs have not been done. | Deviation runs: Kafka down, worker restart while ORDERED, a repeated `paymentSettled`, GoldAccountLinked, a parked Partner Products Outbox. Scripted into Playwright `35-resilience` where the UI can reach them. |
| The same payment command delivered repeatedly; a stale saga reply | Agreed. Fixed in round 1 (R5, R8); the API-level tests are still to be written. | Playwright `40-security`: same Idempotency-Key with other details → 409; out-of-branch account → refused. **Done 22-OCT**: the 409 was already covered by `api/payments-idempotency.spec.ts`; `api/out-of-branch-payment.spec.ts` has a Melbourne agent send a payment from a Sydney account straight to the BFF → Accounts refuses it (`ACCOUNT_NOT_IN_BRANCH`), the payment ends REJECTED, and the Sydney branch cannot read it. |
| Token exchange by an unregistered client; staff member reaching another branch's customer | Agreed. | `40-security`: exchange by a client not on the allow-list; worker endpoints with a user token or another client; out-of-branch records. **Done 22-OCT**, with real IDP tokens: `api/token-exchange-allow-list.spec.ts` (one allowed exchange as the control; a Journey API skipping its BFF, another BFF, clients without the grant and machine tokens are refused) and `api/worker-internal-endpoints.spec.ts` (Partner Products and Payments internal endpoints: no token 401, a staff member's delegated token and another machine 403, the Worker itself passes). |
| Partner Bank patterns B and C tested separately | Agreed. | WireMock adapter tests (C1): refusal, time-out, lost answer and resend with the same `jti`. Partner Bank repository: replayed instruction, staff-view reference redeemed twice. **EWP side done 21-OCT**: `tst/dotnet/PartnerProducts.Api.Tests` (WireMock.Net, 12 tests: signed instruction verified with EWP's public key, refusals with the Partner Bank's reason, 5xx / unreachable / late answer = unavailable, resend with the same `jti`, rejected token renewed) and the Partner Products Journey API's Jest tests against WireMock standalone (9 tests: RFC 8693 exchange per scope, the Domain API's answers translated, no internal text). **Partner Bank side done 22-OCT**: new `tst/PartnerBank.Api.Tests` there (NUnit, 32 tests, no running service): a replayed staff-view instruction refused (also late in its 5 minutes), a `request_uri` redeemed twice or by 32 requests at once opens one page, the same opening / purchase instruction resent returns the same account / order with no second SMS, the same instruction committed by another request between check and save still makes one record (unique index), signature forgeries refused; `tools/JourneyCheck` also resends a signed purchase (70 checks). Its Threat-Model section 5 maps each test to a threat. |
| An altered audit entry or checkpoint is detected | Agreed. The control exists (R-3); the tamper test was deferred. | Tamper test: change an entry and delete a checkpoint, then expect Audit health Unhealthy. **Done 22-OCT**: `audit/audit-tamper-evidence.spec.ts` (a plain UPDATE is refused; an entry edited with the trigger off and a removed checkpoint are each reported by readiness - Degraded, not Unhealthy, because the trail must keep recording - and put back). The test found a gap: a removed checkpoint was hidden when the next one was written (the monitor counted files). Fixed: `AuditChainMonitor` now remembers each checkpoint by its entry. |
| Payment outcome unknown → funds not released without authoritative evidence | **Already in place:** the network status enquiry and the four-eyes reconciliation (`9b6efbf`, `276ce97`). The workflow re-reads the payment instead of trusting the event. | Covered by the deviation runs; no new control. |
| Every non-final state has a detection mechanism and an owner | Agreed for Partner Products. Onboarding (age alerts) and Payments (unconfirmed-saga alert) already have one. | A health check for purchases left ORDERED too long, and a resend tool for parked Partner Products Outbox messages. **Done 21-OCT** (`DigitalGoldAgeHealthCheck`, which also covers a Form VII left SENT; resend on Platform Health). |
| Threat-model-to-test traceability | Agreed: cheap, and it makes the evidence visible. | A "Verified by" column in Threat-Model §7 and the abuse cases. **Done 22-OCT**: every abuse case and residual risk names its unit or Playwright test, or says "code only" / "manual" where none exists yet (two abuse cases added: a replayed MFA code, a token exchange off the allow-list). The gaps found were closed with new unit tests: `IDP.Tests` (9 tests, the one-time MFA codes of fix R2, run on SQLite because the claim is a conditional UPDATE) and the Digital Gold purchase in `PartnerProducts.Api.Tests` (38 tests: slip rules, the same instruction ID on a resend, the payment exactly the slip, PAID only with its own payment, cancelled only while unpaid). |
| Prove the EWP → Partner Bank PAR / JAR / PKCE flow and the resulting customer identity | **Aimed at the wrong pattern for today.** EWP has no customer channel yet. Pattern A is exercised on the Partner Bank side by `tools/HandshakeCheck` and `tools/JourneyCheck`. | Definition of done for A7 + B1, not a gap in the current PoC. |
| Deployment-wide rate limits | Already a production requirement (R-14, item 10 below). Rate limits per instance backed by a shared store would be throwaway work in a PoC. | D-series gateway. |
| Production keys and immutable checkpoints under independent control; production-like configuration check; independent penetration test | Correct for production, and not something a PoC can close. | Items 1, 2 and 17 below; the D-series cloud demo is the production-like environment. |

---

## What remains for a production go-ahead

These are the production requirements from all four rounds. Each is recorded in `doc/Threat-Model.md` §7 or in the component READMEs.

**Keys, secrets and evidence**

1. Document key-encryption keys, the audit signing key, the IDP signing certificate and the Data Protection keys move to a key vault or HSM, with rotation, backup and recovery (R-2, R-3).
2. Audit checkpoints move to immutable (WORM) storage under a separate administrator (R-3).
3. Integration events are signed for audit-grade actor attribution (R-6).
4. Secrets come from a secret store, with secret scanning in CI (R-10).

**Network and deployment**

5. mTLS between services, with certificate-bound tokens (RFC 8705) between services (R-9).
6. Network controls, plus a scrape token or mTLS, for the health and metrics endpoints (R-8).
7. `AllowedHosts` and proxy settings for the real topology.
8. Versioned database migrations instead of the full reset scripts.
9. A SignalR backplane for more than one Notifications instance.
10. Deployment-wide rate limits and connection caps, at a gateway or with a shared store (R-14).

**Operations and monitoring**

11. A security-event stream and dashboards: sign-in failures, MFA lockouts, token-exchange refusals, dead-letter depth.
12. A resend tool for parked Customer Onboarding and KYC Outbox messages (Payments, Accounts and Compliance have one since 15-OCT, Partner Products since 21-OCT).
13. Retention and access rules for stored notifications.

**Load and contracts**

14. A benchmark of the global audit chain under realistic load.
15. A test of the real payment network's idempotency contract (retention window, replayed answers).

**Tests**

16. When automated tests resume:
    - shared token fixtures for the .NET and NestJS stacks (R-11);
    - authorization-matrix tests;
    - CSRF and token-exchange abuse tests;
    - .NET unit tests for the remaining contexts (Customer Onboarding, KYC, Compliance, Documents Management, Audit). Payments, Accounts, Partner Products and the IDP's MFA codes have them.
17. An independent penetration test of the trust boundaries and the financial workflows, and a check of the production configuration in a production-like environment (Microsoft, round 4).