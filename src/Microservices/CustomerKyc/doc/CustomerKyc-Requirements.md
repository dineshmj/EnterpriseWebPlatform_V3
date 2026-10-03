# Customer KYC — Bounded Context Requirements

**Bounded context:** Customer KYC  
**Subdomain type:** Core  
**Status:** Present (first slice) — human review works end to end; the domain model is still anemic

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Customer KYC verifies that a customer is who they claim to be and that their submitted evidence is genuine. It is the first human-approval step of the onboarding workflow.

| Owns | Does not own |
|---|---|
| KYC cases and their lifecycle | The customer profile or the onboarding application (Customer Onboarding) |
| Identity-verification and document-verification stage outcomes | AML screening, risk assessment, compliance decisions (Compliance) |
| KYC decisions, decision makers, remarks and timestamps | Document content and storage (Documents Management — read via M2M) |
| KYC case assignment (target ReBAC data) | |

### Deployable components

| Component | Location | Technology |
|---|---|---|
| KYC MFE | `BFF.Web/client-app` | Next.js static export, served by the KYC BFF |
| KYC BFF | `BFF.Web/src` | **NestJS** (deliberately a different BFF technology from Customer Onboarding) |
| KYC API | `API` | ASP.NET Core 10, EF Core, PostgreSQL. Hosts its own in-process Outbox relay. |
| KYC subscriber | `src/AsyncWorkflows/Subscribers/CustomerKyc` | .NET worker. Part of **this** bounded context: it turns an onboarding event into a KYC command. |
| Database | `EwpKycDb` (`API/KycDb/EwpKycDb.sql`) | PostgreSQL |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| KYC Officer | View the KYC work queue and case details; view the identity proof and tax proof; approve or reject identity verification; approve or reject document verification; *(planned)* request more information, place on hold, mark for remediation | Decide a case for a workflow they initiated; make compliance or AML decisions; change customer data to influence a decision |
| Customer Service Agent | *(planned)* View the KYC status of their customers' applications | Decide anything |
| Auditor | View KYC history and decisions | Change anything |

**Single officer, two stages.** One KYC Officer may currently decide both the identity stage and the document stage of the same case. A four-eyes variant, where each stage must be decided by a different officer, is a planned SoD demonstration.

---

## 3. Permissions

| Permission | KYC Officer | Auditor |
|---|:-:|:-:|
| `kyc.case.view` | ✓ | |
| `kyc.case.update` | ✓ | |
| `kyc.identity.verify` | ✓ | |
| `kyc.document.verify` | ✓ | |
| `kyc.case.request_information` | ✓ | |
| `kyc.case.approve` / `kyc.case.reject` | ✓ | |
| `kyc.case.hold` | ✓ | |
| `kyc.history.view` | | ✓ |

API scopes: `customer-kyc.read`, `customer-kyc.write`.

---

## 4. Domain Model

### 4.1 Current model

`KycCase` is currently an **anemic entity**: statuses are strings, and the decision rules live in `KycCaseService`. Its fields:

- `ApplicationId` (unique) and `ApplicationNumber` — the onboarding application, held by value. The unique `ApplicationId` is the idempotency key for case creation.
- `CustomerNumber` — the application's customer (not unique: one customer can have several applications).
- `Status` — the overall case status.
- Identity-verification stage: status, decided-by user, decided-at, remarks.
- Document-verification stage: status, decided-by user, decided-at, remarks.
- Overall decision: decided-by user, decided-at, remarks. Populated only when the case becomes terminal.
- `InitiatedByUserId` — the onboarding initiator, used for SoD.

The database backs the rules with CHECK constraints: valid statuses, stage metadata present exactly when a stage is decided, and overall-state consistency.

### 4.2 Target model (DDD)

- `KycCase` aggregate root with typed statuses and behaviour (`DecideStage`, `RequestInformation`, `Hold`) that enforces the §6 rules itself.
- `VerificationStage` value or entity (Identity, Document) with its own status and decision.
- A resource `Branch` for ABAC, and `AssignedOfficer` for ReBAC.
- Domain events raised by the aggregate and mapped to integration events by the Outbox.

### 4.3 Ubiquitous language

| Term | Meaning |
|---|---|
| KYC Case | The unit of KYC work for one onboarding application |
| Verification Stage | Identity Verification or Document Verification; each is decided independently |
| Stage Decision | Approve or reject one stage, with remarks |
| Case Decision | The derived overall outcome |
| Work Queue | Cases awaiting review, shared by eligible officers |

---

## 5. States

### 5.1 Stage status (identity and document, independently)

```text
PENDING_REVIEW ──approve──► APPROVED
PENDING_REVIEW ──reject───► REJECTED
```

### 5.2 Case status (derived)

```text
PENDING_REVIEW ──both stages APPROVED──► APPROVED (terminal)
PENDING_REVIEW ──any stage REJECTED────► REJECTED (terminal)
```

Target additional states: `AWAITING_INFORMATION` (more evidence requested; returns to PENDING_REVIEW) and `ON_HOLD`.

---

## 6. Business Rules

1. The stages are independent and may be decided in either order.
2. Rejecting a stage requires remarks. Remarks are at most 4000 characters.
3. Only a stage that is PENDING_REVIEW can be decided. Deciding it again is a 409 Conflict.
4. A terminal case (APPROVED or REJECTED) cannot be decided again.
5. When several officers act on the same case at once, exactly one stage decision wins. Decisions are serialised by a row lock and a conditional update; the others receive 409.
6. **SoD:** the workflow initiator cannot decide any stage of the case.
7. **SoD fails closed (target):** if the initiator is unknown, the decision is denied or escalated, not allowed.
8. Every stage decision emits a stage event. A decision that makes the case terminal also emits `KycCaseApproved` or `KycCaseRejected`, in the same transaction.
9. KYC never stores document content. It reads evidence from Documents Management by business reference and document type.

---

## 7. Integration

| Direction | What | Status |
|---|---|---|
| In | `onboarding.application.submitted` → subscriber → `POST /internal/v1/kyc/cases/from-application-submitted` (M2M): one case per application | Present |
| Out | `kyc.case.created`, `kyc.identity.verification.*`, `kyc.document.verification.*`, `kyc.case.approved`, `kyc.case.rejected`, each carrying `ApplicationId` / `ApplicationNumber` | Present |
| Consumed by | Customer Onboarding (`CustomerOnboardingKycSubscriber`) records `kyc.case.created` / `approved` / `rejected` on the application | Present |
| Sync | KYC BFF → Documents Management (`documents-management.read`, M2M) for the identity proof and tax proof | Present |

Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

---

## 8. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View queue / case | `customer-kyc.read` + role `kyc_officer` + `kyc.case.view` + department `KYC` (+ branch scope, target) |
| Decide identity stage | `customer-kyc.write` + `kyc_officer` + `kyc.case.approve` or `kyc.case.reject` (target: also `kyc.identity.verify`) + department `KYC` + clearance ≥ 3 + not the initiator + stage pending |
| Decide document stage | As above, with `kyc.document.verify` as the target stage permission |
| Create case (internal) | M2M only: pinned `client_id` of the KYC subscriber + `customer-kyc.write` |
| Assigned-case access | Target ReBAC: `assigned_to` the case. The demo queue is intentionally unassigned, so `ethan.kyc` and `noah.kyc` compete for the same case. |

---

## 9. User Interface

| MFE route | Purpose |
|---|---|
| `/v1/kyc/cases/view-all` | KYC work queue |
| `/v1/kyc/cases/view-details` | Case details and decision history |
| `/v1/kyc/identity-verification/view-all` | Identity-verification review (shows the identity proof) |
| `/v1/kyc/documents/view-all` | Document-verification review (shows the tax proof) |

---

## 10. Implementation Status and Known Gaps

| Item | Status |
|---|---|
| Two-stage review, row-locked decisions, CHECK constraints | Present |
| KYC Outbox and in-process relay (7 topics) | Present: `SKIP LOCKED` claiming, per-aggregate ordering, bounded retries with parking, one idempotent producer |
| Department + clearance ABAC and initiator SoD on decisions | Present |
| Stage-specific permissions (`kyc.identity.verify`, `kyc.document.verify`) used in policy | Planned |
| Branch scope and `assigned_to` ReBAC | Planned (the case has no branch or assignee yet) |
| SoD fails closed when the initiator is missing | **Gap**: the check is skipped when `InitiatedByUserId` is null |
| Aggregate-based domain model (§4.2) | Planned |
| One case per application | Present (`uq_kyc_cases_application_id`) |
| Standard event envelope | **Gap**: KYC events are flat |
| Inbox in the subscriber | Planned (idempotency currently relies on the unique `application_id`) |
| Poison-message handling / dead-letter topic in the subscriber | **Gap**: an unprocessable message stops the worker |
| Document lookup | Present: by business reference and document type only (the filename fallback is removed). The officer's branch is passed to Documents Management, so an officer sees only evidence uploaded in their own branch. |
| Safe evidence display | Present: only DM-verified PDF is shown inline (`nosniff`, framable only by the KYC MFE); other types are downloaded; content is streamed |
| BFF session security | Present: session regenerated at sign-in; logout revokes the refresh token and ends the IDP session; front-channel logout (`/signout-oidc`); timing-safe CSRF check; secrets required from the environment (no fallbacks) |

---

## 11. Acceptance Scenarios

- Two officers open the same case. The first decision wins; the second receives 409.
- Approving identity and then document makes the case APPROVED and emits `KycCaseApproved` exactly once.
- Rejecting without remarks is rejected with a validation error.
- The initiator of the onboarding cannot decide either stage.
- An officer with clearance 2, or in a non-KYC department, is denied.
- A `customer-kyc.read`-only token cannot decide.
- Redelivering the same triggering event does not create a second case.
