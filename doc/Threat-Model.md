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
| E | Endpoint without authorization | Deny by default: every endpoint needs a policy | Every API README (deny by default) |
| E | Reading or changing another branch's records (BOLA) | Branch scope on every read and command; payment funds reserved only on an account of the initiator's branch | Payments README #4, #15; Accounts README #13; KYC / CO / DM READMEs |
| E | Privilege combination (auditor + branch role) | The auditor's permission counts only through the Audit Journey API | Payments README #4 |
| E | Insider fraud: self-approval, approval above authority | Separation of duties (maker ≠ checker, across contexts); approval limits by clearance; clearance by risk (Compliance) | Payments README #5, #6; Accounts README #5; Compliance README |
| S | A stolen machine token posts fake workflow steps | Internal endpoints pin the exact `client_id`, not only the scope | Every API README (internal endpoints) |
| T | Duplicate or tampered retries create a second payment | Idempotency-Key bound to the person and the payment's details (409 otherwise); unique constraints | Payments README #7, #13 |
| I | Internals in error responses | Problem details with a trace ID only | Every API README (problem responses) |
| D | One caller floods an API | Per-caller rate limits (person, client, IP) | Every API README (token validation)0 |

### TB4 — APIs ↔ PostgreSQL

| | Threat | Control | Where |
|---|---|---|---|
| E/I | A compromised service reads or changes other contexts' data | One database per context; one login role per deployable with data access to its own database only, no DDL | [EwpServiceDbUsers.sql](../db/EwpServiceDbUsers.sql); every API README (own database user) |
| T/R | Rewriting the audit trail | Append-only (trigger + SELECT/INSERT-only user); hash chain verified continuously; readiness degrades on a break; signed checkpoints outside the database catch a complete rewrite | [Audit API README](../src/Microservices/Audit/API/README.md) #3, #4, #8 |
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
| S | Calls to an impostor endpoint | Configured base URLs + API keys; HTTPS | Payments / Accounts / Compliance configuration |

## 5. Abuse cases (banking)

| Abuse case | How the platform stops it | Proven by |
|---|---|---|
| An agent pays from another branch's customer by calling the API directly | Accounts refuses the reservation (`ACCOUNT_NOT_IN_BRANCH`) | Code (R1); API test pending |
| A payments officer approves their own payment | Separation of duties in the aggregate | Code; unit test held |
| An officer approves above their clearance | Approval limits by clearance | Code; unit test held |
| A stolen password approves a payment | Step-up MFA on decisions, signed in within the last 15 minutes (MFA on outside Development) | Code |
| An auditor browses payments unrecorded | Only through the Audit Journey API, where every look is itself audited | Code; journey asserts pending |
| A forged "funds held" message moves a payment | Kafka ACLs + pinned `client_id` on internal endpoints | Code, ACLs |
| A slow network makes a retry pay twice | Idempotency-Key at the network | Playwright `35-resilience` (paid once) |
| An admin rewrites the audit trail | Detected by the hash chain; a complete recomputation is detected by the signed checkpoints kept outside the database | Integrity monitor |

## 6. Security testing

| Layer | Where |
|---|---|
| End to end (journeys, deciders, audit trail) | `tst/playwright/tests/30-journeys` (onboarding, payments) |
| Resilience (external systems refuse / are down / are slow) | `tst/playwright/tests/35-resilience` |
| Browser security (headers, session revocation) | `tst/playwright/tests/40-security` |
| Unit and adapter tests | Held until the PoC's security and architecture settle |

## 7. Residual risks

| ID | Risk | Rating | Status |
|---|---|---|---|
| R-1 | The payment network call ran inside a database transaction (row lock held up to 20 s) | Medium (availability) | **Fixed**: claim (short transaction, 60 s lease) → call with no transaction → record (short transaction, only if the saga's Version is unchanged) — `RunDueSagaStep.cs` |
| R-2 | KYC documents were not encrypted at rest | High (PII) | **Fixed**: envelope encryption in `EncryptedDocumentStorage.cs`. Remaining in production: key-encryption keys in a key vault / HSM instead of configuration |
| R-3 | The audit hash chain had no anchor outside its database: a privileged administrator could recompute the whole chain | Medium | **Fixed**: signed checkpoints (ECDSA P-256) in write-once files, checked by every verification - `AuditAnchors.cs`. Remaining in production: the signing key in a key vault / HSM and the checkpoints in immutable storage under another administrator |
| R-4 | Approvals required MFA at sign-in, not a recent one | Medium | **Fixed**: step-up requires `amr` mfa AND `auth_time` at most `Mfa:StepUpMaxAgeMinutes` (default 15) old - `MfaStepUp.cs`. Possible polish: an automatic re-authentication (OIDC `max_age` / `acr_values`) instead of "sign out, sign in again" |
| R-5 | No operator tool to resend parked Outbox messages; no reconciliation with the payment network for unknown outcomes | Medium (operations) | Planned |
| R-6 | Audit actor fields come from producer envelopes (trusted producers, Kafka ACLs) — not signed | Low | Accepted for PoC |
| R-7 | SignalR hub had no per-user connection quota | Low | **Fixed**: at most `Notifications:MaxConnectionsPerPerson` (default 10) connections per person, per instance - `NotificationsHub.cs` |
| R-8 | `/metrics` and health endpoints are anonymous (network controls assumed) | Low | Accepted for PoC (deployment control) |
| R-9 | No mTLS between services | Medium in production | Accepted for PoC (service mesh in production) |
| R-10 | Development secrets and certificates in configuration; SASL_PLAINTEXT on localhost | High in production | Accepted for PoC: development only; production uses a secret store, TLS and fail-closed start-up |

## 8. Assumptions and out of scope

- **Local development deployment.** One machine, `*.dev.localhost` host names, development certificates; PostgreSQL and Kafka on localhost.
- **External systems are simulators.** Their own security is out of scope; the platform's controls towards them (TB6) are in scope.
- **Database scripts are destructive bootstrap scripts** for development (no migrations); production would use versioned migrations with approval and backups.
- **No customer-facing channel yet.** All users are staff; a customer channel would add its own threats (credential stuffing at scale, mule accounts) to TB1.