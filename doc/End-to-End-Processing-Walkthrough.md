# End-to-End Processing Walkthrough — Onboarding to KYC Decision

This document traces **one onboarding**, from the agent's submit to the KYC outcome landing back in Customer Onboarding, through the actual components and code. It shows every place a request is authenticated, authorized or checked against a business rule.

It does not define the rules themselves. Those live in:
- [Authorization-Model.md](Authorization-Model.md): how RBAC, ABAC, ReBAC and SoD decisions are made;
- [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md): the cross-context workflow;
- [Integration-Event-Catalogue.md](Integration-Event-Catalogue.md): the event contracts;
- each context's requirements document.

**Legend:**
- **[AuthN]** authentication
- **[RBAC]** role / permission
- **[ABAC]** attribute: scope, department, clearance, branch
- **[ReBAC]** relationship
- **[SoD]** separation of duties
- **[Domain]** business rule
- **[CSRF]** forgery protection

**Actors** (demo users, see [IDP-Requirements §6](../src/IDP/doc/IDP-Requirements.md#6-demo-data)):
- `sophie.cs`: Customer Service Agent, branch SYD001;
- `ethan.kyc` and `noah.kyc`: KYC Officers, branch SYD001.

---

## Phase 1: the agent submits the onboarding (synchronous)

```text
Sophie (browser)
  └─ Shell (Next.js) ── hosts the CO MFE in an iframe (CSP frame-ancestors: Shell + IDP only)
       └─ CO MFE (Next.js static) ── multipart POST /onboarding/applications
            + X-CSRF header + session cookie (no tokens in the browser)
            │
            ▼
CO BFF (ASP.NET Core + Duende BFF)                          OnboardingController.CreateAndSubmit
  [AuthN]  server-side session → Sophie's access token (Authorization Code + PKCE, refresh rotation)
  [CSRF]   [ValidateAntiForgeryToken] + Duende X-CSRF header check
  checks   both files present and PDF; size ≤ 50 MB
  [ABAC]   Sophie has a branch claim (else 403: documents could never be read again)
  creates  WorkflowId + CorrelationId (one per human action)
  │
  ├─①  POST CO API /v1/customers   (Sophie's USER token + X-Workflow-Id / Correlation / Causation)
  ├─②  POST CO API /v1/onboarding/applications {customerId}
  ├─③  POST DM API /v1/documents ×2 (KYCProof, TaxProof)  (Sophie's token EXCHANGED for DM: sub Sophie, branch SYD001, act CO BFF)
  ├─④  GET  CO API /v1/onboarding/applications/{id}   (read Version)
  └─⑤  POST CO API /v1/onboarding/applications/{id}/submit {expectedVersion}
       (if ③–⑤ fail: compensate = delete the uploaded documents)
```

### ① Create the customer (Customer Onboarding API)

```text
JWT validation [AuthN] → global "ApiScope" policy
→ "CustomerWrite": scope customer-onboarding.write [RBAC] + role customer_service_agent [RBAC]
→ CustomersController: residential address within Sophie's branch city/country [ABAC] (else 403)
   managing agent := Sophie's sub [ReBAC: the relationship is created here]
→ CreateCustomerCommandHandler: CustomerNumber from DB sequence, clock.GetUtcNow()
→ Domain: PersonName / EmailAddress / PhoneNumber / PostalAddress value objects validate [Domain]
          Customer.Create(…, managingAgent) → raises CustomerCreated
          AddAddress → "at most one primary address" [Domain]
→ CustomerDbContext.SaveChanges (one transaction):
     customers + customer_addresses rows
     → CustomerIntegrationEventMapper → outbox row "CustomerCreated" (envelope, workflow IDs, initiator)
```

### ② Create the application

```text
"OnboardingWrite" (write scope + agent role) [RBAC]
→ CanManageCustomerAsync: customer within branch scope [ABAC] (else 404)
                          AND Sophie is its managing agent [ReBAC] (else 403)
→ branch := Sophie's branch claim (SYD001)
→ handler: ApplicationNumber.Issue(next application_number_seq, now) → APP-yyyyMMdd-nnnnnn
→ Domain: customer.StartOnboarding (PROSPECT → ONBOARDING) [Domain]
          OnboardingApplication.Create → ApplicationRef = UUID v7, status DRAFT
→ SaveChanges: both aggregates in one transaction (the documented exception);
  CustomerStatusChanged is raised but internal, so no outbox row
```

### ③ Upload the documents (Documents Management API, token exchange)

```text
"DocumentWrite" policy (scope) [RBAC]
→ DocumentResourceAuthorization: a person (sub) AND act.client_id = CO or KYC BFF [delegation, RFC 8693]
     → branch claim of the exchanged token (issued by the IDP) → BranchCode SYD001 (else 403)
→ UploadDocumentCommandHandler: FileName VO (path / reserved characters stripped), BranchCode VO
→ DocumentContentPolicy: magic bytes must be PDF/PNG/JPEG and match the declared type [Domain]
→ storage (SHA-256) → Document.Upload(…, branch) → DocumentUploaded (internal) → documents row
   (stored file deleted if the DB save fails)
```

### ⑤ Submit

```text
"OnboardingWrite" [RBAC] → CanManageApplicationAsync: branch scope [ABAC] + managing agent [ReBAC]
→ handler: expected Version must match (optimistic concurrency) [Domain]
→ Domain: application.Submit(now): only from DRAFT [Domain]
          raises OnboardingApplicationSubmitted (ApplicationRef, number, BranchCode)
          then StatusChanged DRAFT → SUBMITTED
→ SaveChanges: application row + 2 outbox rows, inserted one by one (sequence = cause before effect)
→ 204 → CO BFF returns 201 to the MFE
```

---

## Phase 2: asynchronous hand-off to KYC (choreography)

```text
CustomerOutboxPublisher (console worker)
  SELECT … FOR UPDATE SKIP LOCKED   (safe with several instances)
  oldest unpublished message per aggregate, in sequence order; retries → park
  → Kafka "onboarding.application.submitted"  key = aggregate ID  (idempotent producer, acks=all)
        │
        ▼
KycCaseOpeningSubscriber (console worker, KYC's adapter)
  validates the envelope: ApplicationRef, ApplicationNumber, CustomerNumber, BranchCode
  Client Credentials token (CustomerKyc.Subscriber client)
  → POST KYC API /internal/v1/kyc/cases/from-application-submitted
        "KycCaseOpeningSubscriberWrite": write scope + PINNED client_id [client pinning]
        → OpenKycCaseCommandHandler: existing case for this ApplicationRef? → return it (idempotent)
        → Domain: KycCase.Open(ref, number, customer, BranchCode SYD001, initiator Sophie)
                  both stages PENDING_REVIEW, unassigned → raises KycCaseOpened
        → KycDbContext: case row + outbox "KycCaseCreated" (mapper: ApplicationRef, BranchCode)
           unique application_ref settles concurrent duplicates
  commits the Kafka offset only after success
        │
KYC API in-process outbox relay → Kafka "kyc.case.created"
        │
        ▼
OnboardingOutcomeSubscriber (console worker, CO's adapter)
  → POST CO API /internal/v1/onboarding/applications/{ApplicationRef}/kyc-outcomes
        "OnboardingOutcomeSubscriberWrite": write scope + PINNED client_id
        X-Initiated-By-User-Id is trusted only from this client (attribution, not authorization)
        → Inbox: MessageId already processed? → Duplicate
        → load by ApplicationRef; ApplicationNumber must match (else 409 → dead-letter topic)
        → Domain: RecordKycCaseOpened(now): SUBMITTED → KYC_IN_PROGRESS (tolerant of order/repeats)
        → one transaction: inbox row + application + outbox "StatusChanged"
  transient failure → retry in place (Seek); permanent → customer-onboarding.outcome-subscriber.dlq
```

---

## Phase 3: the KYC officer reviews and decides (synchronous)

```text
Ethan → Shell → KYC MFE (iframe)
  → KYC BFF (NestJS): session (regenerated at login) [AuthN], timing-safe CSRF check [CSRF]
     → KYC API with Ethan's USER token
```

### View the queue and the evidence

```text
GET /v1/kyc/cases
  "KycCaseView": read scope + kyc_officer + kyc.case.view [RBAC] + department KYC [ABAC]
  → controller: Ethan's branch claim, else 403 [ABAC, fails closed]
  → IKycCaseQueries.GetCasesAsync(SYD001, …): only SYD001 cases [ABAC]
GET evidence: KYC BFF → DM (Ethan's token exchanged: branch SYD001, act KYC BFF, read only) → branch match, else 404 [ABAC]
  → verified PDF only shown inline (nosniff); other types downloaded
```

### Approve the identity stage

```text
POST /v1/kyc/cases/{id}/identity-verification/approve
  "KycIdentityApprove": write scope + kyc_officer [RBAC]
       + kyc.case.approve AND kyc.identity.verify [RBAC, stage permission]
       + department KYC + clearance ≥ 3 [ABAC]
  → controller: branch claim present [ABAC]
  → DecideVerificationStageCommandHandler (one transaction):
       SELECT … FOR UPDATE (row lock: concurrent deciders queue here)
       load KycCase → case branch == Ethan's branch, else 404 [ABAC]
  → Domain: KycCase.DecideStage
       officer identified                                     [Domain]
       initiator unknown → 403; initiator == Ethan → 403      [SoD, fails closed]
       case terminal → 409                                    [Domain]
       assigned to someone else → 403                         [ReBAC]
       stage not pending → 409; reject without remarks → 400  [Domain]
       unassigned → assigned to Ethan (KycCaseAssigned, internal)  [ReBAC relationship created]
       stage APPROVED; case status derived (still PENDING_REVIEW); Version + 1
       raises VerificationStageDecided
  → SaveChanges: version check (2nd concurrency guard) + outbox "KycIdentityVerificationApproved"
```

**Another officer tries the document stage.** If Noah tries next, the same checks pass up to `assigned to someone else`, and the aggregate refuses with **403 [ReBAC]**. An officer can also take a case explicitly beforehand with `POST …/claim`, and the assignee can return it with `…/release` (policy `KycCaseAssign`).

**The assigned officer approves the document stage.** Both stages are now APPROVED, so the case becomes **APPROVED** and the final decision fields are set. The aggregate raises **VerificationStageDecided** and then **KycCaseDecided**. The mapper writes two outbox rows: the stage event first, then `KycCaseApproved`, whose `CausedByMessageIds` lists both stage events.

---

## Phase 4: the outcome returns to Customer Onboarding

```text
KYC relay → Kafka "kyc.case.approved"
  → OnboardingOutcomeSubscriber → CO API …/{ApplicationRef}/kyc-outcomes (pinned client)
     → Inbox → number match → RecordKycApproved(now): KYC_IN_PROGRESS → KYC_COMPLETED
       (also handles "approved" arriving before "created")
     → inbox + application + outbox "StatusChanged" in one transaction
  → CustomerOutboxPublisher → "onboarding.application.status.changed" (no consumer: Notifications reads the KYC, Compliance and Accounts events directly)
```

## Phase 5: Compliance

```text
KYC relay → Kafka "kyc.case.approved" (now also carries BranchCode and both KYC stage deciders)
  → ComplianceCaseOpeningSubscriber → Compliance API internal/v1/compliance/cases/from-kyc-approved (pinned client)
     → Inbox + ComplianceCase.Open (SCREENING) + outbox "ComplianceCaseCreated" in one transaction
  → OnboardingOutcomeSubscriber → CO API …/{ApplicationRef}/compliance-outcomes → KYC_COMPLETED → COMPLIANCE_IN_PROGRESS

Compliance screening worker (every 5 s, one due case at a time, FOR UPDATE SKIP LOCKED)
  → external screening provider (timeout, retry, circuit breaker)
     ok      → RecordScreeningResult: risk LOW / MEDIUM / HIGH, required clearance 3 / 4 / 5 → UNDER_REVIEW
     failure → RecordScreeningFailure: stays SCREENING, retried with back-off (never a pass)

Compliance officer (Compliance MFE → Compliance BFF → Compliance API) → claim / hold / approve / reject
  → aggregate: branch, assignment (ReBAC), SoD (not the initiator, not a KYC decider), clearance ≥ risk (ABAC)
  → outbox "ComplianceCaseApproved" / "Rejected" → OnboardingOutcomeSubscriber → CO: COMPLIANCE_COMPLETED / REJECTED
```

---

## Phase 6: Accounts — the onboarding completes

```text
Compliance relay → Kafka "compliance.case.approved" (carries the Compliance approver, DecisionByUserId)
  → AccountApplicationOpeningSubscriber → Accounts API internal/v1/accounts/applications/from-compliance-approved (pinned client)
     → Inbox + AccountApplication.Open (PENDING_REVIEW) + outbox "AccountApplicationCreated" in one transaction
  → OnboardingOutcomeSubscriber → CO API …/{ApplicationRef}/account-outcomes → COMPLIANCE_COMPLETED → ACCOUNT_OPENING_IN_PROGRESS

Account officer (jack.accounts: Accounts MFE → Accounts BFF → Accounts API) → claim / hold / approve (product) / reject
  → aggregate: branch, assignment (ReBAC), SoD (not the initiator, not the Compliance approver)
  → approve: OPENING (the decision's command ID is kept as the cause of what follows)

Account-opening worker (every 5 s, one due application at a time, FOR UPDATE SKIP LOCKED)
  → core-banking system POST /v1/accounts with Idempotency-Key = ApplicationRef (timeout, retry, circuit breaker)
     opened  → application OPENED + Account (BSB 062-000, account number) + outbox "AccountOpened"
               → OnboardingOutcomeSubscriber → CO: ACCOUNT_OPENING_IN_PROGRESS → COMPLETED (the saga ends)
     failure → stays OPENING, retried with back-off; 6 failures or a refusal → FAILED + "AccountOpeningFailed"
               → OnboardingOutcomeSubscriber → CO: COMPENSATING → REJECTED (RejectedBy ACCOUNT_OPENING)
               → "OnboardingApplicationRejected" → DM invalidates the evidence; the customer is a prospect again
```

---

## Phase 7: a rejection is compensated

```text
KYC (Ethan), Compliance (Olivia / Grace) or Accounts (Jack) rejects
  → OnboardingOutcomeSubscriber → CO API: application → REJECTED
     → outbox "OnboardingApplicationRejected" (RejectedBy, BranchCode, the evidence document IDs recorded at submission)
       and "StatusChanged", in one transaction
  → CustomerOutboxPublisher → Kafka "onboarding.application.rejected"
  → DocumentInvalidationSubscriber → DM API internal/v1/documents/invalidations (pinned client)
     → Inbox + Document.Invalidate for each named document of the same branch, in one transaction
       (status INVALIDATED, reason "Onboarding application APP-… rejected by KYC"; retained, delete now refused)
```

---

## Alongside every phase: notifications

```text
kyc.case.created / compliance.case.screened / accounts.application.created
  → NotificationsSubscriber → Notifications API: "New KYC case …" for staff:kyc_officer:SYD001 (and so on per team)
kyc.case.approved|rejected, compliance.case.*, accounts.application.rejected, accounts.account.opened|opening.failed
  → NotificationsSubscriber → Notifications API: "etpar approved KYC for Camilla Parkers …" for user:{initiator}
  → Inbox + notifications rows (one transaction) → pushed over SignalR to the audience's connections
  → Shell BFF proxies the hub and /bff/notifications with the person's token
  → Shell: bell + toast; relays BSS_NOTIFICATION to the MFE in the frame (work queues reload on new work)
```

## Where each authorization type is decided

| Type | Where | Examples |
|---|---|---|
| **RBAC** | ASP.NET policies (`Program.cs` of each API) | Scopes, roles, `kyc.case.approve` + `kyc.identity.verify` |
| **ABAC** | Policy handlers, resource-authorization classes, query filters | Department / clearance (KYC policy); branch scope (CO `CustomerAccessScope`, KYC `InBranch`, DM `Document.BelongsTo`) |
| **ReBAC** | Customer Onboarding: `CustomerResourceAuthorization`, against the customer's managing agent. KYC: inside the `KycCase` aggregate. | Managing agent; assigned officer |
| **SoD** | Inside the `KycCase` aggregate | The initiator can't claim or decide; an unknown initiator is denied |
| **Client pinning** | Policies on internal endpoints; the acting client (`act`) in DM | Only named M2M clients on internal endpoints, trusted headers (`X-Initiated-By-User-Id`) accepted only from them; in DM only the CO and KYC BFFs may act for a person |
| **Workflow state** | Aggregates | Transition table, terminal states, version checks |

## Known gaps on this path

- **Kafka is authenticated but not encrypted locally.** Every component connects as its own SCRAM user with deny-by-default ACLs, so only the Customer Onboarding relay can publish a submission and only the KYC API can publish KYC outcomes ([kafka/README.md](../kafka/README.md)). Local development uses `SASL_PLAINTEXT`; production needs `SASL_SSL`.
- **Every hop is traced and probed:** one OpenTelemetry trace follows the onboarding through the Outbox and Kafka (`traceparent` header), and every component exposes `/health/live` and `/health/ready`.
- **Every subscriber shares one reliable consume loop** (`AsyncWorkflows.Infrastructure.Subscribers`): transient failures are retried in place, permanent ones go to the worker's own dead-letter topic, and each receiving API records each message in its Inbox.
- **The KYC MFE has no claim / release buttons;** the first decision assigns the case.

*Keep this walkthrough in step with the code: update it when a step, policy or rule on this path changes.*