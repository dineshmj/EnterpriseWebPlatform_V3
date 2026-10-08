# Customer KYC — Bounded Context Requirements

**Bounded context:** Customer KYC  
**Subdomain type:** Core  
**Status:** Present (first slice) — human review works end to end on a `KycCase` aggregate

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
| KYC Case Opening Subscriber | `src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber` | .NET worker. Part of **this** bounded context: it turns an onboarding event into a KYC command. |
| Database | `EwpKycDb` (`API/KycDb/EwpKycDb.sql`) | PostgreSQL |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| KYC Officer | View the KYC work queue and case details; view the identity proof and tax proof; approve or reject identity verification; approve or reject document verification; *(planned)* request more information, place on hold, mark for remediation | Decide a case for a workflow they initiated; make compliance or AML decisions; change customer data to influence a decision |
| Customer Service Agent | *(planned)* View the KYC status of their customers' applications | Decide anything |
| Auditor | View KYC history and decisions, in the Audit Trail (Audit context) | Use this context's screens or API; change anything |

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

### 4.1 Model

| Element | Kind | Responsibility |
|---|---|---|
| `KycCase` (`API/Domain/Aggregates`) | Aggregate root | `Open(...)` and `DecideStage(stage, decision, officer, remarks, now)`. Enforces every §6 rule itself, including SoD. Raises the domain events. `Version` is its optimistic-concurrency token. |
| `VerificationStage` | Value object (EF complex type) | Status, decided-by, decided-at and remarks of one stage. Immutable: `Decide(...)` returns a new value and refuses a stage that is not pending. |
| `DecisionRemarks` | Value object | Trimmed, never blank, at most 4000 characters. |
| `KycCaseStatus`, `VerificationStatus`, `VerificationStageType`, `StageDecision` | Typed enums | Persisted and published as explicit codes (`PENDING_REVIEW`, `IDENTITY_VERIFICATION`…), never as names or ordinals. |
| `KycCaseOpened`, `VerificationStageDecided`, `KycCaseDecided` | Domain events | Translated to the published `kyc.*` events by `KycIntegrationEventMapper`, in the same transaction (Outbox). |

The case holds the onboarding application by value: `ApplicationRef` (Customer Onboarding's never-repeating GUID, unique here) and `ApplicationNumber`. It also holds the `CustomerNumber` (not unique: one customer can have several applications), `BranchCode` (the branch the application was opened in; ABAC), `AssignedOfficerUserId` (ReBAC `assigned_to`) and `InitiatedByUserId` (SoD).

**Layers.** Controllers translate HTTP only. The Application layer holds the command handlers (`OpenKycCase`, `DecideVerificationStage`) and the read side (`IKycCaseQueries`, projections without loading aggregates). Infrastructure holds the EF mapping, the repository and the unit of work. **Concurrency:** a decision locks the case row and then loads the aggregate; the `version` column is a second line of defence. Of six simultaneous decisions on one stage, exactly one wins and the others receive 409.

The database backs the rules with CHECK constraints: valid statuses, stage metadata present exactly when a stage is decided, and overall-state consistency.

### 4.2 Next steps

- Behaviour for `RequestInformation` and `Hold` (§5 target states).
- A claim / release action in the KYC MFE (the API endpoints exist; the UI currently assigns implicitly on the first decision).
- Four-eyes per stage (a different officer per stage), as an SoD variant of the assignment rule.

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
7. **SoD fails closed:** if the initiator is unknown, the decision is denied (403), never allowed.
8. Every stage decision emits a stage event. A decision that makes the case terminal also emits `KycCaseApproved` or `KycCaseRejected`, in the same transaction.
9. KYC never stores document content. A case records exactly the two evidence documents submitted with its application (their Documents Management IDs, from `onboarding.application.submitted`) and the officer reviews those documents, never "the latest" of the customer. A case is not opened without them (fail closed).
10. A case also records the applicant **as submitted** (name and residential address, no contact details): the identity the officer verifies the evidence against. It is a snapshot and never refreshed, so the case keeps showing what was verified even if the customer's record changes later. It is passed on to Compliance in `kyc.case.approved`.

---

## 7. Integration

| Direction | What | Status |
|---|---|---|
| In | `onboarding.application.submitted` → subscriber → `POST /internal/v1/kyc/cases/from-application-submitted` (M2M): one case per application | Present |
| Out | `kyc.case.created`, `kyc.identity.verification.*`, `kyc.document.verification.*`, `kyc.case.approved`, `kyc.case.rejected`, each carrying `ApplicationRef` / `ApplicationNumber` | Present |
| Consumed by | Customer Onboarding (`OnboardingOutcomeSubscriber`) records `kyc.case.created` / `approved` / `rejected` on the application | Present |
| Sync | KYC BFF → Documents Management (`documents-management.read`, M2M) for the identity proof and tax proof | Present |

Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

---

## 8. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View queue / case | `customer-kyc.read` + role `kyc_officer` + `kyc.case.view` + department `KYC` + **branch scope**: only cases whose `BranchCode` equals the officer's `branch` claim (another branch's case is a 404; no branch claim = 403) |
| Decide identity stage | `customer-kyc.write` + `kyc_officer` + `kyc.case.approve` or `kyc.case.reject` **and** `kyc.identity.verify` + department `KYC` + clearance ≥ 3 + own branch + **assigned to the officer, or unassigned (the decision assigns it)** + not the initiator + stage pending |
| Decide document stage | As above, with `kyc.document.verify` |
| Claim / release (`POST …/claim`, `…/release`) | `customer-kyc.write` + `kyc_officer` + `kyc.case.update` + department `KYC` + clearance ≥ 3 + own branch. Claim: case unassigned (409 if assigned to someone else), not the initiator. Release: only the assignee (403 otherwise). |
| Create case (internal) | M2M only: pinned `client_id` of the KYC Case Opening Subscriber + `customer-kyc.write` |
| Assigned-case access | ReBAC `assigned_to`, stored on the case and checked in the aggregate. New cases start unassigned in the branch queue, so `ethan.kyc` and `noah.kyc` compete; the first to decide (or claim) owns the case from then on. |

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
| Stage-specific permissions (`kyc.identity.verify`, `kyc.document.verify`) used in policy | Present |
| Branch scope and `assigned_to` ReBAC | Present (`branch_code`, `assigned_officer_user_id`; claim / release endpoints) |
| SoD fails closed when the initiator is missing | Present (enforced in the aggregate) |
| Aggregate-based domain model (§4.1) | Present |
| One case per application | Present (`uq_kyc_cases_application_ref`: a GUID, so a recreated Customer Onboarding database cannot collide with old cases) |
| Standard event envelope | **Gap**: KYC events are flat |
| Inbox / idempotent consumer | Present: `inbox_messages` (unique `message_id` + `consumer`) is written in the same transaction as the new case and its Outbox event; one case per `ApplicationRef` as well |
| Consumer resilience | Present: timeout, retry with jitter and circuit breaker on the API call; cached M2M token; transient failures retried in place; permanent failures to `customer-kyc.case-opening-subscriber.dlq` (shared consume loop) |
| Document lookup | Present: by the document IDs the case recorded at submission (no search by customer number or file name). The officer's branch is passed to Documents Management, so an officer sees only evidence uploaded in their own branch. |
| Safe evidence display | Present: only DM-verified PDF is shown inline (`nosniff`, framable only by the KYC MFE); other types are downloaded; content is streamed |
| BFF session security | Present: session regenerated at sign-in; `SameSite=Lax` session cookie; logout revokes the refresh token and ends the IDP session; front-channel (`/signout-oidc`) and back-channel (`/backchannel-logout`, fully validated logout token) logout; timing-safe CSRF check; strict CSP with hashed inline scripts; secrets required from the environment (no fallbacks). Sessions are in memory (single instance). |
| Evidence display hardening | Decision: the PDF preview is **not** sandboxed, because Chrome refuses to render PDFs in sandboxed frames. Instead: magic-byte verification in DM, inline display only for verified PDF, `nosniff`, framable only by the KYC MFE. |

---

## 11. Acceptance Scenarios

- Two officers open the same case. The first decision wins; the second receives 409.
- Approving identity and then document makes the case APPROVED and emits `KycCaseApproved` exactly once.
- Rejecting without remarks is rejected with a validation error.
- The initiator of the onboarding cannot decide either stage.
- An officer with clearance 2, or in a non-KYC department, is denied.
- A `customer-kyc.read`-only token cannot decide.
- Redelivering the same triggering event does not create a second case.