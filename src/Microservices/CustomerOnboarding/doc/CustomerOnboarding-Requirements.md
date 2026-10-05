# Customer Onboarding — Bounded Context Requirements

**Bounded context:** Customer Onboarding (CO)  
**Subdomain type:** Core  
**Status:** Present — the most mature context in EWP V3

Platform-wide rules (authorization mechanics, event envelope, saga rules) are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Customer Onboarding owns the **customer** and the **onboarding application** — the record of a customer's request to become a banking customer, and its progress through the onboarding workflow.

| Owns | Does not own |
|---|---|
| Customer profile, contact details, addresses, customer lifecycle status | KYC verification results (Customer KYC) |
| Onboarding applications and their workflow status | Compliance / AML decisions (Compliance) |
| Initiation of the onboarding workflow (`WorkflowId`, `CorrelationId`, initiator) | Document content and storage (Documents Management) |
| Customer–agent relationship (ReBAC `manages`: the managing agent) | Accounts (Accounts) |

### Deployable components

| Component | Location | Technology |
|---|---|---|
| CO MFE | `BFF.Web/client-app` | Next.js static export, served by the CO BFF |
| CO BFF | `BFF.Web` | ASP.NET Core 10 + Duende BFF |
| CO API | `API` | ASP.NET Core 10, EF Core, PostgreSQL |
| CO Outbox relay | `src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher` | .NET worker service. Reads `EwpCustomerDb`, so it is **part of this bounded context**, not a shared component. |
| Onboarding Outcome Subscriber | `src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber` | .NET worker service. Consumes `kyc.case.*` and records each outcome through the CO API (M2M). **Part of this bounded context.** |
| Database | `EwpCustomerDb` (`API/CustomerDB/EwpCustomerDb.sql`) | PostgreSQL |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Customer *(self-service channel planned)* | Start their own onboarding; enter and update their own details while the application is in Draft; upload required documents; submit; view the status of their own application | Approve any stage; see another customer's data; use internal functions |
| Customer Service Agent | Create customers and applications on a customer's behalf; review and correct permitted customer data; submit applications; view onboarding workflow status; view KYC status where permitted | Make KYC, compliance or account-opening decisions; access customers outside their organizational scope; approve their own submissions |
| Operations Administrator | View applications and workflow status; perform technical recovery | Create or change business data; approve |
| Auditor | Read customer and onboarding history | Change anything |

---

## 3. Permissions

| Permission | Customer | CSA | Auditor |
|---|:-:|:-:|:-:|
| `customer.onboarding.create` | ✓ | ✓ | |
| `customer.onboarding.view_own` / `customer.onboarding.update_own` | ✓ | | |
| `customer.onboarding.view` / `customer.onboarding.update` / `customer.onboarding.assist` | | ✓ | |
| `customer.onboarding.submit` | ✓ | ✓ | |
| `customer.profile.view` / `customer.profile.update` | | ✓ | |
| `customer.history.view` | | | ✓ |

API scopes: `customer-onboarding.read`, `customer-onboarding.write`.

---

## 4. Domain Model

### 4.1 Aggregates

**`Customer`** (aggregate root)

- Identity: database ID plus `CustomerNumber` (value object, generated from a sequence).
- Attributes: first and last name, `EmailAddress`, `PhoneNumber`, `CustomerType` (`Individual`, `Business`), `CustomerStatus`, optional `BranchId`, optional `SubjectId` (the customer's own OIDC subject, if any), `Version`.
- Child entity: `CustomerAddress` with a `PostalAddress` value object and an `AddressType` (`Residential`, `Business`, `Mailing`).
- Raises: `CustomerCreated`.

**`OnboardingApplication`** (aggregate root)

- Identity: an internal database ID; `ApplicationRef` (UUID v7, the identity **other contexts** use: it never repeats, even when the database is recreated); and `ApplicationNumber` (value object, issued by this context as `APP-yyyyMMdd-nnnnnn` from a database sequence).
- References its customer **by ID only** (`CustomerId`). The customer is not part of the application aggregate.
- `BranchCode`: the branch the application was opened in (the acting agent's `branch` claim). Published so that KYC can scope its work queue.
- Attributes: `OnboardingApplicationStatus`, `SubmittedAt`, `CompletedAt`, `Version` (optimistic concurrency).
- `EvidenceDocuments`: the Documents Management documents submitted with the application (document ID + type, table `onboarding_application_documents`), by ID only.
- Raises: `OnboardingApplicationSubmitted`, `OnboardingApplicationStatusChanged`, `OnboardingApplicationRejected` (names the evidence documents, so Documents Management can invalidate them).
- Reacts to KYC facts through `RecordKycCaseOpened`, `RecordKycApproved` and `RecordKycRejected`. These are tolerant of repeated and out-of-order facts: they apply only the transitions still outstanding, and report whether anything changed.

### 4.2 Invariants

| Rule | Enforced in |
|---|---|
| First and last name are required | `Customer` |
| A customer has at most one primary address | `Customer.AddAddress` |
| An email address is required, at most 254 characters, and contains `@` | `EmailAddress` |
| A phone number is required and at most 30 characters | `PhoneNumber` |
| Address line 1, city, state, postal code and country code are required | `PostalAddress` |
| A closed or suspended customer cannot start onboarding | `Customer.StartOnboarding` |
| Only a customer in Onboarding can be activated | `Customer.Activate` |
| A closed customer cannot be suspended | `Customer.Suspend` |
| An application needs a valid customer | `OnboardingApplication.Create` |
| An application can be submitted only from Draft | `OnboardingApplication.Submit` |
| An application is submitted together with its evidence (at least one document, each once) | `OnboardingApplication.Submit` |
| A rejection names the evidence for compensation | `OnboardingApplication.Reject` |
| A submission must carry the version the user last saw | `SubmitOnboardingApplicationCommandHandler` |
| Status transitions follow §5.2; terminal states are final | `OnboardingApplication` |

### 4.3 Ubiquitous language

| Term | Meaning |
|---|---|
| Customer | A person or business known to the bank, from prospect to closed |
| Customer Number | The bank-facing customer identifier, shared with other contexts as a reference |
| Onboarding Application | One request to onboard a customer, carried through KYC, compliance and account opening |
| Application Number | The bank-facing identifier of an onboarding application |
| Submission | The moment an application leaves Draft and the distributed workflow starts |
| Initiator | The authenticated human who submitted. Recorded as `initiated_by`. |

---

## 5. States

### 5.1 Customer status

```text
PROSPECT ──StartOnboarding──► ONBOARDING ──Activate──► ACTIVE
   any (except CLOSED) ──Suspend──► SUSPENDED
                                    CLOSED (terminal)
```

### 5.2 Onboarding application status

```text
DRAFT ─Submit─► SUBMITTED ─► KYC_IN_PROGRESS ─► KYC_COMPLETED ─► COMPLIANCE_IN_PROGRESS
      ─► COMPLIANCE_COMPLETED ─► ACCOUNT_OPENING_IN_PROGRESS ─Complete─► COMPLETED

Any non-terminal ──Reject──► REJECTED
Any non-terminal ──Cancel──► CANCELLED
Any non-terminal ──StartCompensation──► COMPENSATING ──► COMPENSATION_FAILED
```

| State | Meaning |
|---|---|
| DRAFT | Being prepared. Editable by the customer or the agent. |
| SUBMITTED | Submitted; normal editing is closed; awaiting KYC |
| KYC_IN_PROGRESS | A KYC case exists and is being worked |
| KYC_COMPLETED | KYC approved |
| COMPLIANCE_IN_PROGRESS | Compliance and AML review under way |
| COMPLIANCE_COMPLETED | Compliance approved |
| ACCOUNT_OPENING_IN_PROGRESS | The Accounts context is opening the account |
| COMPLETED | Terminal. All steps succeeded. |
| REJECTED | Terminal. A business decision rejected the application; the deciding event is recorded. |
| CANCELLED | Terminal. Intentionally withdrawn. |
| COMPENSATING | A later step failed; earlier work is being compensated |
| COMPENSATION_FAILED | Terminal for automation. Compensation failed and needs operational recovery. |

### 5.3 How CO reacts to other contexts (choreography)

| Incoming event | CO reaction | Status |
|---|---|---|
| `KycCaseCreated` (for this application) | SUBMITTED → KYC_IN_PROGRESS | Present |
| `KycCaseApproved` | (SUBMITTED →) KYC_IN_PROGRESS → KYC_COMPLETED; ignored when already beyond | Present |
| `KycCaseRejected` | SUBMITTED / KYC_IN_PROGRESS → REJECTED; ignored after KYC_COMPLETED | Present |
| `ComplianceCaseCreated` | (… →) KYC_COMPLETED → COMPLIANCE_IN_PROGRESS | Present |
| `ComplianceCaseApproved` / `Rejected` | → COMPLIANCE_COMPLETED / REJECTED | Present |
| `AccountOpened` / `AccountOpeningFailed` | → COMPLETED / COMPENSATING | Planned |

Event contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md). Overall workflow: [Saga plan](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md).

---

## 6. Business Rules

1. **Required documents.** An application can be submitted only with an identity proof (`KYCProof`) and a tax proof (`TaxProof`). Both must be PDF, which Documents Management verifies from the file signature. The CO API never receives document content: the BFF uploads it to Documents Management and only references are kept.
2. **Residential address.** A new customer is created with exactly one primary residential address. It determines the serving branch, and so who may see the customer.
3. **The customer's identity is not the staff user's identity.** When an agent creates a customer, the customer's `SubjectId` stays empty until the customer's own identity is linked through an explicit identity-association step. It must never be taken from the agent's token or accepted unchecked from the caller.
4. **The workflow starts at the human action.** The CO BFF creates `WorkflowId` and `CorrelationId` when the user submits. The CO API records the user's `sub` as `initiated_by` on every Outbox row of that workflow.
5. **Business state and Outbox are atomic.** Customer or application changes and their Outbox rows commit in one database transaction.
6. **Partial submission failure.** If the submission sequence fails after the customer or application was created, those records remain (the operation is not falsely reported as atomic). Documents already uploaded in the same request are removed. This is request-level cleanup of something never submitted, not saga compensation.
7. **Compensation on rejection.** When KYC or Compliance rejects the application, CO publishes `OnboardingApplicationRejected` with the evidence document IDs recorded at submission. Documents Management invalidates exactly those documents (retained, not deleted). CO never changes another context's data.

---

## 7. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View a customer or application | Permission **and** branch scope (ABAC). A Customer Service Agent sees customers whose primary residential address is in the agent's branch city and country (`branch_city` / `branch_country_code` claims). Operations administrators, platform administrators and auditors read globally. Anyone else, or an agent without branch location claims, sees nothing (404). |
| Update a customer; open or submit its application | Branch scope **and** ReBAC `manages`: only the customer's managing agent (`customers.managing_agent_user_id`). Another agent of the same branch gets 403; another branch gets 404. |
| Create a customer | `customer_service_agent` + write scope + the residential address is within the agent's branch scope (otherwise 403). The creating agent becomes the managing agent. |
| Submit | `customer.onboarding.submit` + application in DRAFT + version match |
| Customer self-service | `_own` permissions + the customer `owns` the application |
| Workflow-driven transitions (§5.3) | Only through `POST /internal/v1/onboarding/applications/{applicationRef}/kyc-outcomes` (the application addressed by its GUID; the application number must match), pinned to the `CustomerOnboarding.OutcomeSubscriber.To.CustomerOnboardingApi.M2M.ClientID` client with `customer-onboarding.write`; never via user endpoints. That client alone may state the human initiator (`X-Initiated-By-User-Id`), so the resulting events keep the original initiator for attribution. |

---

## 8. User Interface

| MFE route | Purpose |
|---|---|
| `/v1/customers/view-all` | Customer list and details |
| `/v1/onboarding/applications/view-all` | Applications, plus the onboarding form (create customer, upload documents, submit) |
| `/v1/onboarding/workflow/view-all` | Workflow progress (Outbox activity and IDs) |

---

## 9. Implementation Status and Known Gaps

| Item | Status |
|---|---|
| Aggregates, value objects, domain events, CQRS handlers | Present |
| Transactional Outbox with workflow, correlation and causation IDs and `initiated_by` | Present |
| Outbox relay publishing all three event types | Present: `SKIP LOCKED` claiming (multi-instance safe), per-aggregate ordering, bounded retries with exponential backoff, parking after `MaxAttempts`, idempotent `acks=all` producer |
| Reactions to KYC events (§5.3) | Present (`OnboardingOutcomeSubscriber`) |
| Reactions to Compliance / Accounts events | Planned |
| Inbox / idempotent consumer | Present: `inbox_messages` (unique `message_id` + `consumer`) is written in the same transaction as the transition and its Outbox events; a redelivered KYC event returns `Duplicate` |
| Consumer resilience | Present: timeout, retry with jitter and circuit breaker on the API call; transient failures retried in place; permanent failures to `customer-onboarding.outcome-subscriber.dlq` |
| API scope enforced per operation | Present (read policies require `customer-onboarding.read`, write policies `customer-onboarding.write`) |
| Role-based endpoint policies | Present |
| Branch-scoped object-level authorization | Present (`CustomerResourceAuthorization` + `CustomerAccessScope`): applied to single reads, lists (filtered in the database), updates, application creation and submission; out-of-scope resources return 404 |
| `manages` relationship check | Present (managing agent stored on the customer; enforced on update, application creation and submission) |
| Managing-agent reassignment (agent leaves, workload balancing) | Planned |
| Operations and platform administrators excluded from business writes | Present (writes require `customer_service_agent`) |
| `SubjectId` / `BranchId` protected from caller input | Present (no longer accepted by `CreateCustomerRequest`) |
| Primary residential address captured at creation | Present (required for new customers; drives branch scope) |
| Application number generation | Present: issued by the CO API (`application_number_seq`); callers cannot supply it |
| Cross-context identity | Present: `ApplicationRef` (GUID) on every application event; KYC outcomes are routed by it |
| Workflow headers | Read by `WorkflowContextAccessor` (infrastructure), not by the DbContext or the domain |

---

## 10. Acceptance Scenarios

- An agent creates a customer and an application, uploads both PDFs and submits. The application is SUBMITTED and three Outbox rows share one `WorkflowId`, with causation chaining.
- Submitting with a stale version is rejected.
- Submitting an application that is not in DRAFT is rejected.
- A token holding only `customer-onboarding.read` cannot create or submit.
- An agent from another branch cannot read or update the customer.
- Another agent of the same branch can read the customer but cannot update it or open / submit its application (403).
- `operations_administrator` and `platform_administrator` cannot create or update customers.
- An `auditor` can read history but receives 403 on any write.