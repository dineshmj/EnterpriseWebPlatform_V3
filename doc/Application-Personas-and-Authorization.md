# Enterprise Web Platform V3
## Business Requirements — Application Personas & Authorization

**Version:** 1.0  
**Status:** Draft  
**Domain:** Banking Services  

---

## 1. Purpose

The Enterprise Web Platform V3 PoC represents a banking-services platform in which different categories of users perform different business activities.

The PoC application must ensure that:

1. Users can access only the functionality appropriate to their role.
2. Sensitive business operations require appropriate authorization.
3. Approval responsibilities are separated where appropriate.
4. Access may depend not only on a user's role, but also on attributes such as branch, department, clearance level, and employment type.
5. Access to individual business entities may depend on relationships such as branch ownership, customer assignment, or KYC case assignment.
6. Service-to-service communication is authenticated independently from human-user authorization.
7. Significant business actions and approvals are auditable.

The personas defined below provide the **business requirements** from which the application's RBAC, ABAC and ReBAC authorization policies will be derived.

---

# 2. Application Personas

| Persona | Primary Responsibility |
|---|---|
| Customer Service Agent | Creates and manages customer onboarding applications |
| KYC Officer | Performs identity and document verification |
| Compliance Officer | Performs compliance, AML and risk-related decisions |
| Account Officer | Reviews and approves account-opening activities |
| Payments Officer | Reviews and processes payment instructions |
| Operations Administrator | Performs operational administration and exception handling |
| Auditor | Reviews business activity and audit history |
| Customer | Initiates and reviews their own customer-facing activities |

These personas represent **business responsibilities**, rather than technical implementation roles.

A single physical employee may possess more than one application role where permitted by organizational policy. However, incompatible roles and approval responsibilities should be subject to **Separation of Duties** rules.

---

# 3. Customer

## 3.1 Responsibilities

A Customer represents an end user of the banking platform.

The Customer may:

- Begin a customer onboarding application.
- Enter and update their personal information.
- Provide contact and address information.
- Upload required documents.
- Review information before submission.
- Submit an onboarding application.
- View the status of their own application.
- View their own accounts once opened.
- Initiate eligible payment activities.

The Customer must not:

- Approve their own KYC verification.
- Approve their own compliance decision.
- Approve their own account opening.
- Access another customer's information.
- Access internal operational functions.

---

# 4. Customer Service Agent

## 4.1 Responsibilities

The Customer Service Agent is responsible for assisting customers with onboarding and customer information.

The Customer Service Agent may:

- Create an onboarding application on behalf of a customer.
- Review customer information.
- Correct permitted customer information.
- Submit customer applications for processing.
- View the status of onboarding workflows.
- View customer-related KYC status where permitted.
- Assist with incomplete applications.

The Customer Service Agent must not:

- Perform final KYC approval.
- Perform AML/compliance approval.
- Approve an account opening where the same agent initiated the application, subject to Separation of Duties.
- Approve their own submitted changes.
- Access customers outside their permitted organizational scope.

---

# 5. KYC Officer

## 5.1 Responsibilities

The KYC Officer is responsible for verifying customer identity and submitted documentation.

The KYC Officer may:

- Review KYC cases.
- Perform identity verification.
- Review identity-provider results.
- Review submitted documents.
- Request additional documentation.
- Approve or reject identity verification.
- Approve or reject document verification.
- Mark a KYC case as requiring remediation.
- View relevant customer information required for verification.

The KYC Officer must not:

- Approve their own KYC work where Separation of Duties requires independent approval.
- Perform final AML/compliance approval unless explicitly authorized.
- Modify unrelated customer information merely to influence a KYC decision.

---

# 6. Compliance Officer

## 6.1 Responsibilities

The Compliance Officer is responsible for compliance and financial-crime-related decisions.

The Compliance Officer may:

- Review KYC results.
- Review AML screening results.
- Review risk assessments.
- Investigate compliance exceptions.
- Request additional verification.
- Approve or reject a compliance decision.
- Place an application on compliance hold.
- Release an application from compliance hold where permitted.
- Escalate suspicious or high-risk cases.

The Compliance Officer must not:

- Bypass mandatory KYC requirements.
- Modify verification results produced by verification services.
- Approve their own compliance decision where independent approval is required.
- Access information outside their authorized organizational scope.

---

# 7. Account Officer

## 7.1 Responsibilities

The Account Officer is responsible for account-opening activities following successful customer onboarding and compliance processing.

The Account Officer may:

- Review account-opening requests.
- Verify that prerequisite workflow steps have completed.
- Review customer eligibility information.
- Approve account opening.
- Reject account-opening requests.
- Place account-opening requests on hold.
- View account status and lifecycle information.

The Account Officer must not:

- Open an account when mandatory KYC/compliance prerequisites have failed.
- Override a compliance rejection without an authorized exception process.
- Approve their own account-opening request where Separation of Duties applies.

---

# 8. Payments Officer

## 8.1 Responsibilities

The Payments Officer is responsible for operational processing of payment instructions.

The Payments Officer may:

- Review payment instructions.
- Validate payment information.
- Review payment risk/status information.
- Approve eligible payments.
- Reject payments.
- Place payments on hold.
- Release payments from operational holds where authorized.
- Review payment processing failures.

Payment processing should support different approval requirements depending on the payment amount, risk classification, and organizational policy.

For example:

```text
Low-value payment
    → Standard validation
    → Processing

High-value payment
    → Validation
    → Payments Officer approval
    → Processing

Exception / high-risk payment
    → Validation
    → Compliance review
    → Payments Officer approval
    → Processing
```

The exact thresholds are application business rules and should be configurable rather than hard-coded into the UI.

---

# 9. Operations Administrator

## 9.1 Responsibilities

The Operations Administrator is responsible for operational management of the platform.

The Operations Administrator may:

- Monitor workflow status.
- Investigate failed workflows.
- Review failed messages and processing errors.
- Retry eligible operations.
- Manage operational exceptions.
- View service health information.
- Review workflow and integration status.
- Manage selected reference/configuration data.

The Operations Administrator should **not automatically have unrestricted business approval authority** merely because the persona has extensive technical access.

For example:

> An Operations Administrator may be permitted to retry a failed workflow but should not thereby gain permission to approve a KYC case or authorize a payment.

This distinction demonstrates the separation between **technical/operational administration** and **business authorization**.

---

# 10. Auditor

## 10.1 Responsibilities

The Auditor provides independent read-only access for audit and investigation purposes.

The Auditor may:

- Search audit records.
- View customer workflow history.
- View KYC decisions.
- View account-opening decisions.
- View payment processing history.
- View approval history.
- View workflow correlation information.
- Review who performed a business operation.
- Review when an operation occurred.
- Review the outcome of an operation.

The Auditor should have **read-only access** to business information and must not:

- Modify customer information.
- Approve workflows.
- Reject workflows.
- Retry business operations.
- Modify audit records.

---

# 11. Customer Onboarding Workflow

The principal demonstration workflow for V3 is Customer Onboarding.

A typical onboarding lifecycle is:

```text
Application Created
       │
       ▼
Customer Information Completed
       │
       ▼
Submitted
       │
       ▼
KYC Verification
       │
       ▼
Compliance / AML Review
       │
       ▼
Account Opening
       │
       ▼
Completed
```

The workflow may also enter exception or compensation paths:

```text
                    ┌─── KYC Failed ───────► Rejected
                    │
Submitted ──────────┼─── Compliance Failed ─► Rejected
                    │
                    └─── Account Opening Failed
                                      │
                                      ▼
                              Compensation
```

Each transition must be authorized according to the responsible business persona.

---

# 12. Separation of Duties

The platform should demonstrate **Separation of Duties (SoD)** for sensitive business operations.

For example:

```text
Customer Service Agent
        │
        │ submits
        ▼
   Onboarding
        │
        ▼
   KYC Officer
        │
        │ verifies
        ▼
Compliance Officer
        │
        │ approves compliance
        ▼
 Account Officer
        │
        │ approves account opening
        ▼
     Account
```

A user should not be able to perform conflicting actions simply because they possess multiple permissions.

For example:

- The person who submitted a KYC case should not independently approve that same case where SoD applies.
- The person who performed a compliance review should not independently approve the resulting account opening where policy requires separation.
- A customer must never approve their own onboarding.
- An Operations Administrator should not gain business approval privileges merely through administrative access.

The authorization layer must therefore consider both:

```text
Who is the user?
        +
What role does the user have?
        +
What operation are they attempting?
        +
What business entity is involved?
        +
What is the current workflow state?
        +
What actions has the user already performed?
```

---

# 13. RBAC Requirements

Role-Based Access Control defines the coarse-grained permissions associated with each persona.

Examples:

| Operation | Customer | CSA | KYC | Compliance | Account | Payments | Ops | Auditor |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Create onboarding | ✓ | ✓ | | | | | | |
| Edit customer data | Own | ✓ | | | | | | |
| Perform KYC verification | | | ✓ | | | | | |
| Approve KYC | | | ✓* | | | | | |
| Perform AML review | | | | ✓ | | | | |
| Approve compliance | | | | ✓ | | | | |
| Approve account opening | | | | | ✓ | | | |
| Approve payment | | | | | | ✓ | | |
| Operational retry | | | | | | | ✓ | |
| View audit history | Own | Limited | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Modify audit records | | | | | | | | ✗ |

`✓*` represents a business rule that may require an independent KYC reviewer depending on the workflow configuration.

The final permission matrix should be maintained as a business artifact and subsequently translated into application authorization policies.

---

# 14. ABAC Requirements

Role alone is insufficient to determine access.

The platform should demonstrate **Attribute-Based Access Control**.

Relevant attributes may include:

### User attributes

```text
department
branch
region
employmentType
clearanceLevel
employeeId
```

### Resource attributes

```text
customerId
branchId
riskLevel
classification
paymentAmount
workflowStatus
```

### Environmental attributes

```text
requestTime
channel
authenticationStrength
```

Example:

```text
KYC Officer
    +
KYC permission
    +
same branch
    +
sufficient clearance
    +
case is in KYC-reviewable state
    =
access permitted
```

Whereas:

```text
KYC Officer
    +
KYC permission
    +
different branch
    +
no cross-branch authorization
    =
access denied
```

---

# 15. ReBAC Requirements

The platform should also demonstrate **Relationship-Based Access Control**.

Authorization may depend upon the relationship between the user and the resource.

Examples:

```text
User ──works-at──► Branch
User ──assigned-to──► KYC Case
Agent ──manages──► Customer
Officer ──owns──► Workflow
Customer ──owns──► Account
```

For example, a Customer Service Agent may have permission to view customer records generally, but access to a particular customer may additionally require:

```text
Agent
   └── assigned-to ──► Customer
```

Similarly:

```text
KYC Officer
   └── assigned-to ──► KYC Case
```

This allows V3 to demonstrate the distinction between:

**RBAC**

> "Are you a KYC Officer?"

**ABAC**

> "Are you a KYC Officer with sufficient clearance and within the permitted branch?"

**ReBAC**

> "Are you the KYC Officer assigned to this particular case?"

---

# 16. Service-to-Service Authorization

Human-user authorization and service authorization are separate concerns.

A typical request may look like:

```text
Browser
   │
   │ User authentication
   ▼
Customer BFF
   │
   │ User context
   ▼
Customer API
```

For service-to-service operations:

```text
KYC Service
   │
   │ M2M access token
   ▼
AML Provider
```

or:

```text
Account Service
   │
   │ M2M access token
   ▼
Account-related Service
```

Machine-to-machine authentication must not automatically grant the service unrestricted access to business data.

The receiving service must still authorize:

```text
Calling Service
        +
Requested Operation
        +
Target Resource
        +
Current Workflow State
```

---

# 17. Approval Principles

The following principles apply to sensitive workflows:

1. **No approval without prerequisite completion.**
2. **A failed prerequisite cannot be bypassed through the UI.**
3. **Approval must be associated with an authenticated identity.**
4. **Approval must be auditable.**
5. **The approval timestamp must be recorded.**
6. **The relevant workflow/correlation ID must be recorded.**
7. **The approval decision must be immutable from an audit perspective.**
8. **Separation of Duties rules must be enforced server-side.**
9. **UI visibility must not be treated as authorization.**
10. **Retries and operational actions must not implicitly constitute business approval.**

---

# 18. Audit Requirements

The platform must maintain an audit trail for significant business operations.

An audit event should contain information such as:

```json
{
  "timestamp": "...",
  "userId": "...",
  "role": "...",
  "service": "...",
  "action": "ApproveKyc",
  "entityType": "KycCase",
  "entityId": "...",
  "result": "Approved",
  "correlationId": "...",
  "workflowId": "..."
}
```

Audit records should allow an auditor to answer:

- Who performed the action?
- What did they do?
- Which business entity was affected?
- When did it happen?
- What was the result?
- Which workflow did it belong to?
- Which request/correlation ID was involved?

---

# 19. Business Requirements → Technical Demonstrations

The personas are intentionally designed so that V3 can demonstrate several enterprise security patterns.

| Business Requirement | Technical Demonstration |
|---|---|
| Different responsibilities | RBAC |
| Branch/department restrictions | ABAC |
| Assigned-case access | ReBAC |
| Independent approvals | Separation of Duties |
| Service authentication | M2M OAuth |
| Workflow state restrictions | State-based authorization |
| Approval history | Audit |
| Distributed workflow | Saga |
| Reliable events | Outbox |
| Idempotent consumers | Inbox |
| Workflow notifications | SignalR |
| Failure handling | Retry / Circuit Breaker / Compensation |
| Traceability | Correlation ID / Distributed tracing |

---

# 20. Guiding Principle

The V3 platform should follow this principle:

> **A user should be authorized based not merely on who they are, but on what they are responsible for, what they are permitted to do, their relationship to the business resource, and the current state of the business process.**

This principle provides the business foundation for implementing **RBAC + ABAC + ReBAC + workflow authorization + Separation of Duties** within the Enterprise Web Platform V3 PoC.

---

## Suggested Next Requirements Artifacts

The following documents can be derived from this one:

- **Permission Matrix** — detailed permissions for every persona and business operation.
- **Approval Workflow Requirements** — exact states, transitions, approvers, rejection and compensation paths.
- **Authorization Rules** — RBAC, ABAC, ReBAC and Separation of Duties rules.
- **Business Entity & Relationship Model** — Customer, KYC Case, Account, Payment, Branch, Employee and their relationships.
- **Audit Requirements** — events and information that must be recorded.
