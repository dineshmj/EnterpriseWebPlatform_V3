# Enterprise Web Platform V3
## Authorization Model

**Version:** 1.0  
**Domain:** Banking Services  
**Status:** Draft  
**Purpose:** Business and authorization requirements for the Enterprise Web Platform V3 PoC

---

## 1. Purpose

This document defines the authorization model for the Enterprise Web Platform V3 Banking Services PoC.

It establishes the relationship between:

```text
Persona
   ↓
Role
   ↓
Permission
   ↓
ABAC Attributes
   ↓
ReBAC Relationships
   ↓
Workflow State
   ↓
Approval / Separation of Duties
   ↓
Authorization Decision
```

The document is intended to serve as the business-level source of truth from which the Identity Provider (IDP), authorization policies, API authorization handlers, workflow implementation, test cases, and UI behavior are derived.

The V3 PoC is not intended to represent a production banking platform. The banking domain is used to provide realistic enterprise scenarios in which distributed workflows, authorization, security, resilience, auditability, and architectural patterns can be demonstrated.

---

# 2. Authorization Principles

The authorization model follows these principles:

1. Authentication establishes **who the user is**.
2. RBAC establishes **what broad responsibilities the user has**.
3. Permissions establish **what operations the user may perform**.
4. ABAC determines whether the operation is permitted under relevant user, resource, and environmental attributes.
5. ReBAC determines whether the user has the required relationship with the target business resource.
6. Workflow state determines whether the requested operation is valid at that point in the business process.
7. Separation of Duties prevents conflicting operations from being performed by the same user where required.
8. Authorization must be enforced server-side.
9. UI visibility is not authorization.
10. Operational privileges must not automatically confer business approval privileges.
11. Service-to-service authentication is separate from human-user authorization.
12. Significant authorization decisions and business approvals must be auditable.
13. Authorization failures must not reveal unnecessary business or security information.

---

# 3. Personas

The principal application personas are:

| Persona | Description |
|---|---|
| Customer | End customer using customer-facing banking services |
| Customer Service Agent | Assists customers with onboarding and customer information |
| KYC Officer | Performs identity and document verification |
| Compliance Officer | Performs AML, compliance, and risk-related decisions |
| Account Officer | Reviews and approves account-opening activities |
| Payments Officer | Reviews and processes payment instructions |
| Operations Administrator | Performs operational monitoring and exception handling |
| Auditor | Performs independent read-only audit and investigation activities |
| Platform Administrator | Administers the PoC platform and selected technical configuration |

A persona represents a business responsibility. It is not necessarily identical to a physical employee, organizational job title, or technical role.

---

# 4. Roles

The initial V3 RBAC model defines the following roles.

| Role Code | Role Name | Primary Persona |
|---|---|---|
| `customer` | Customer | Customer |
| `customer_service_agent` | Customer Service Agent | Customer Service Agent |
| `kyc_officer` | KYC Officer | KYC Officer |
| `compliance_officer` | Compliance Officer | Compliance Officer |
| `account_officer` | Account Officer | Account Officer |
| `payments_officer` | Payments Officer | Payments Officer |
| `operations_administrator` | Operations Administrator | Operations Administrator |
| `auditor` | Auditor | Auditor |
| `platform_administrator` | Platform Administrator | Platform Administrator |

A user may possess more than one role where organizational policy permits it. Multiple roles do not automatically bypass Separation of Duties requirements.

---

# 5. Permission Model

Permissions represent individual business or operational capabilities.

The recommended naming convention is:

```text
<bounded-context>.<resource>.<action>
```

Examples:

```text
customer.onboarding.create
customer.onboarding.view
kyc.case.approve
payment.approve
audit.view
```

Permissions are independent of roles.

A role receives permissions through the role-permission relationship.

```text
User
  ↓
Role
  ↓
Permission
```

This allows permissions to be reused by multiple roles without duplicating authorization logic.

---

# 6. Customer Permissions

The Customer may have the following permissions:

```text
customer.onboarding.create
customer.onboarding.view_own
customer.onboarding.update_own
customer.onboarding.submit
customer.account.view_own
customer.payment.create
customer.payment.view_own
```

The `_own` designation indicates that RBAC alone is insufficient. Resource ownership must also be evaluated.

For example:

```text
customer.account.view_own
```

requires both:

```text
Customer role
+
Customer owns the Account
```

---

# 7. Customer Service Agent Permissions

```text
customer.onboarding.create
customer.onboarding.view
customer.onboarding.update
customer.onboarding.submit
customer.onboarding.assist
customer.profile.view
customer.profile.update
workflow.status.view
```

The Customer Service Agent does not receive KYC approval or Compliance approval permissions merely because the agent can view the corresponding workflow.

---

# 8. KYC Officer Permissions

```text
kyc.case.view
kyc.case.update
kyc.identity.verify
kyc.document.verify
kyc.case.request_information
kyc.case.approve
kyc.case.reject
kyc.case.hold
workflow.status.view
```

KYC approval additionally requires:

- appropriate workflow state;
- appropriate organizational attributes;
- required resource relationship;
- Separation of Duties checks.

---

# 9. Compliance Officer Permissions

```text
compliance.case.view
compliance.case.review
compliance.aml.review
compliance.risk.assess
compliance.case.request_information
compliance.case.approve
compliance.case.reject
compliance.case.hold
compliance.case.release
workflow.status.view
```

A Compliance Officer must not bypass mandatory KYC prerequisites.

---

# 10. Account Officer Permissions

```text
account.application.view
account.application.review
account.application.approve
account.application.reject
account.application.hold
account.lifecycle.view
workflow.status.view
```

Account approval requires successful completion of mandatory preceding workflow stages.

---

# 11. Payments Officer Permissions

```text
payment.view
payment.validate
payment.approve
payment.reject
payment.hold
payment.release
payment.retry
workflow.status.view
```

Payment approval may additionally depend on:

- payment amount;
- payment risk classification;
- payment status;
- user's organizational attributes;
- approval level;
- Separation of Duties.

---

# 12. Operations Administrator Permissions

```text
workflow.view
workflow.retry
workflow.pause
workflow.resume
workflow.reprocess
integration.status.view
service.health.view
outbox.view
inbox.view
consumer.status.view
```

The Operations Administrator does not automatically receive:

```text
kyc.case.approve
compliance.case.approve
account.application.approve
payment.approve
```

This is an intentional separation between **technical/operational authority** and **business approval authority**.

---

# 13. Auditor Permissions

The Auditor receives read-only permissions:

```text
audit.view
audit.search
workflow.history.view
customer.history.view
kyc.history.view
account.history.view
payment.history.view
approval.history.view
```

The Auditor must not receive business mutation permissions.

---

# 14. Platform Administrator Permissions

The Platform Administrator is responsible for administration of the PoC environment.

Potential permissions include:

```text
platform.configuration.view
platform.configuration.update
platform.user.view
platform.user.manage
platform.role.view
platform.role.manage
platform.permission.view
platform.health.view
```

Platform administration must not implicitly grant business approval permissions.

For example:

```text
platform_administrator
```

must not automatically imply:

```text
kyc.case.approve
payment.approve
account.application.approve
```

This distinction is important for demonstrating least privilege.

---

# 15. Permission Matrix

The initial coarse-grained permission matrix is:

| Capability | Customer | CSA | KYC | Compliance | Account | Payments | Ops | Auditor | Platform Admin |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Create onboarding | ✓ | ✓ | | | | | | | |
| View onboarding | Own | ✓ | ✓ | ✓ | ✓ | | ✓ | ✓ | ✓ |
| Update onboarding | Own | ✓ | Limited | | | | | | |
| Submit onboarding | ✓ | ✓ | | | | | | | |
| Verify identity | | | ✓ | | | | | | |
| Verify documents | | | ✓ | | | | | | |
| Approve KYC | | | ✓* | | | | | | |
| Review AML | | | | ✓ | | | | | |
| Approve compliance | | | | ✓* | | | | | |
| Approve account | | | | | ✓* | | | | |
| Validate payment | | | | | | ✓ | | | |
| Approve payment | | | | | | ✓* | | | |
| Retry workflow | | | | | | | ✓ | | |
| View audit | Own/Limited | Limited | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Modify audit | | | | | | | | ✗ | ✗ |

`*` indicates that the permission alone is not sufficient. Additional authorization rules apply.

---

# 16. ABAC Model

RBAC determines the user's broad capabilities. ABAC adds contextual restrictions.

ABAC decisions may evaluate:

```text
User Attributes
+
Resource Attributes
+
Environmental Attributes
```

## 16.1 User Attributes

The initial user authorization profile may include:

```text
employeeId
department
branch
region
employmentType
clearanceLevel
```

Example:

```text
employeeId       = EMP-10042
department       = KYC
branch           = BLR001
region           = SOUTH
employmentType   = FULL_TIME
clearanceLevel   = 3
```

---

## 16.2 Resource Attributes

Depending on the bounded context, resources may expose attributes such as:

```text
customerId
branchId
riskLevel
classification
paymentAmount
workflowStatus
assignedOfficerId
```

These attributes are owned by the relevant business microservice.

The IDP must not become the owner of Customer, KYC Case, Account, or Payment business entities.

---

## 16.3 Environmental Attributes

Where appropriate, authorization may also consider:

```text
requestTime
channel
authenticationStrength
```

The PoC should use environmental attributes selectively rather than creating artificial complexity.

---

# 17. ABAC Examples

## 17.1 Branch Restriction

A KYC Officer may access a case when:

```text
Role = kyc_officer
+
Permission = kyc.case.view
+
User.Branch = KycCase.Branch
```

If cross-branch access is not authorized:

```text
User.Branch != KycCase.Branch
```

results in denial.

---

## 17.2 Clearance Restriction

A high-risk KYC case may require:

```text
clearanceLevel >= requiredClearanceLevel
```

Example:

```text
KYC Case Risk = HIGH
Required Clearance = 3
User Clearance = 2
```

Result:

```text
DENY
```

---

## 17.3 Payment Amount

Payment approval may depend on amount:

```text
PaymentAmount
+
User clearance
+
Approval permission
```

For example, a payment exceeding a configured approval threshold may require a higher authorization level or additional approval.

The exact thresholds should be configuration rather than UI constants.

---

# 18. ReBAC Model

Relationship-Based Access Control evaluates relationships between users and resources.

Examples include:

```text
User ──works_at──────► Branch
User ──assigned_to───► KYC Case
Agent ──manages──────► Customer
Officer ──assigned_to─► Workflow
Customer ──owns──────► Account
Customer ──owns──────► Payment
```

ReBAC should complement RBAC and ABAC rather than replace them.

---

# 19. ReBAC Examples

## 19.1 Customer Ownership

To view an account:

```text
Role = customer
+
Permission = customer.account.view_own
+
Customer ──owns──► Account
```

---

## 19.2 Assigned KYC Case

To approve a KYC case:

```text
Role = kyc_officer
+
Permission = kyc.case.approve
+
KYC Officer ──assigned_to──► KYC Case
```

Additional ABAC and SoD checks still apply.

---

## 19.3 Customer Service Assignment

A Customer Service Agent may be permitted to manage a customer when:

```text
Agent ──manages──► Customer
```

or through an organizational relationship such as:

```text
Agent ──works_at──► Branch
Customer ──belongs_to──► Branch
```

The exact relationship used should be determined by the business rule being demonstrated.

---

# 20. Ownership of ReBAC Information

Business relationships should normally be owned by the bounded context that owns the underlying business entity.

For example:

```text
Customer Service
    owns Customer assignment relationships

KYC Service
    owns KYC Case assignment relationships

Accounts Service
    owns Account ownership relationships

Payments Service
    owns Payment relationships
```

For the V3 PoC, a simplified authorization relationship store may be maintained by the IDP for demonstration purposes.

However, business microservices remain the authoritative owners of their business entities.

The IDP must not contain:

```text
Customers
KycCases
Accounts
Payments
```

as duplicated business master data.

---

# 21. Workflow Authorization

Authorization must consider workflow state.

A valid permission does not mean that an operation is valid at every point in the workflow.

For example:

```text
kyc.case.approve
```

is meaningful only when the KYC case is in an approval-ready state.

Conceptually:

```text
Role
+
Permission
+
ABAC
+
ReBAC
+
Workflow State
+
SoD
=
Authorization Decision
```

---

# 22. Customer Onboarding Workflow States

The primary Customer Onboarding workflow uses the following conceptual states:

```text
DRAFT
   ↓
SUBMITTED
   ↓
KYC_IN_PROGRESS
   ↓
KYC_COMPLETED
   ↓
COMPLIANCE_IN_PROGRESS
   ↓
COMPLIANCE_COMPLETED
   ↓
ACCOUNT_OPENING_IN_PROGRESS
   ↓
COMPLETED
```

Terminal or exception states include:

```text
REJECTED
CANCELLED
COMPENSATING
COMPENSATION_FAILED
```

---

# 23. Customer Onboarding State Definitions

## DRAFT

The application is being created or edited.

Permitted activities include:

```text
Customer: create/update own information
Customer Service Agent: create/update/assist
```

The application has not yet entered the formal approval workflow.

---

## SUBMITTED

The customer or authorized Customer Service Agent has submitted the application.

Normal editing is restricted.

The workflow is ready for downstream processing.

---

## KYC_IN_PROGRESS

The KYC process is executing.

Typical activities:

```text
Identity verification
Document verification
KYC review
```

---

## KYC_COMPLETED

KYC requirements have successfully completed.

The workflow may proceed to compliance.

---

## COMPLIANCE_IN_PROGRESS

Compliance and AML activities are being performed.

Typical activities:

```text
AML screening
Risk assessment
Compliance review
```

---

## COMPLIANCE_COMPLETED

Compliance requirements have successfully completed.

The workflow may proceed to account opening.

---

## ACCOUNT_OPENING_IN_PROGRESS

The Accounts bounded context is creating or preparing the account.

---

## COMPLETED

All mandatory onboarding steps have successfully completed.

The customer onboarding workflow is complete.

---

## REJECTED

The application has been rejected.

A rejection must identify the relevant business decision and be auditable.

---

## CANCELLED

The workflow has been intentionally cancelled.

---

## COMPENSATING

A downstream failure requires previously completed actions to be reversed or compensated.

---

## COMPENSATION_FAILED

Compensation itself has failed and requires operational investigation.

This state is particularly useful for demonstrating Saga failure handling.

---

# 24. KYC Case States

The KYC bounded context may use:

```text
CREATED
IN_PROGRESS
AWAITING_INFORMATION
AWAITING_REVIEW
APPROVED
REJECTED
ON_HOLD
```

Typical transitions:

```text
CREATED
   ↓
IN_PROGRESS
   ↓
AWAITING_REVIEW
   ├──► APPROVED
   └──► REJECTED
```

or:

```text
IN_PROGRESS
   ↓
AWAITING_INFORMATION
   ↓
IN_PROGRESS
```

---

# 25. Compliance Case States

The Compliance bounded context may use:

```text
CREATED
SCREENING
UNDER_REVIEW
AWAITING_INFORMATION
APPROVED
REJECTED
ON_HOLD
```

A compliance approval must not occur while mandatory screening is incomplete.

---

# 26. Account Application States

The Accounts bounded context may use:

```text
CREATED
PENDING_REVIEW
APPROVED
OPENING
OPENED
REJECTED
FAILED
```

Account opening requires successful completion of mandatory onboarding and compliance prerequisites.

---

# 27. Payment States

The Payments bounded context may use:

```text
INITIATED
VALIDATING
PENDING_APPROVAL
APPROVED
PROCESSING
COMPLETED
FAILED
REJECTED
CANCELLED
ON_HOLD
```

Example:

```text
INITIATED
    ↓
VALIDATING
    ↓
PENDING_APPROVAL
    ↓
APPROVED
    ↓
PROCESSING
    ↓
COMPLETED
```

Failure paths may lead to:

```text
FAILED
```

or:

```text
ON_HOLD
```

depending on the nature of the failure.

---

# 28. Approval Rules

Approval operations must satisfy all applicable conditions.

A generic approval decision is:

```text
1. User is authenticated
2. User has required role
3. User has required permission
4. User satisfies ABAC requirements
5. User satisfies ReBAC requirements
6. Resource is in an approvable workflow state
7. Prerequisite steps are complete
8. Separation of Duties checks succeed
9. Required approval level is satisfied
10. Operation is not already completed
```

If any mandatory condition fails:

```text
Authorization = DENIED
```

---

# 29. KYC Approval Rule

A KYC approval requires:

```text
Role = kyc_officer
+
Permission = kyc.case.approve
+
Department = KYC
+
Required clearance
+
Permitted branch/resource scope
+
Assigned-to relationship where applicable
+
KYC status = AWAITING_REVIEW
+
SoD check passed
```

---

# 30. Compliance Approval Rule

A compliance approval requires:

```text
Role = compliance_officer
+
Permission = compliance.case.approve
+
Department = COMPLIANCE
+
Required clearance
+
Required AML screening completed
+
Required risk assessment completed
+
Compliance status = UNDER_REVIEW
+
SoD check passed
```

---

# 31. Account Approval Rule

An account-opening approval requires:

```text
Role = account_officer
+
Permission = account.application.approve
+
Required KYC completion
+
Required compliance completion
+
Account application = PENDING_REVIEW
+
SoD check passed
```

---

# 32. Payment Approval Rule

A payment approval requires:

```text
Role = payments_officer
+
Permission = payment.approve
+
Payment status = PENDING_APPROVAL
+
User satisfies amount/risk authorization requirements
+
User satisfies branch/organizational restrictions
+
SoD check passed
```

Higher-value or higher-risk payments may require additional authorization.

---

# 33. Separation of Duties

Separation of Duties prevents a single user from performing conflicting actions.

The PoC should demonstrate both:

### Static SoD

Certain role combinations may be considered incompatible.

Example:

```text
kyc_officer
+
compliance_officer
```

may be disallowed for the same user if the business scenario requires complete separation between verification and compliance decisions.

### Dynamic SoD

A role may be valid for the user, but a particular action is denied because of what the user already did in the current workflow.

Example:

```text
User A submitted / performed KYC work
             ↓
User A attempts KYC approval
             ↓
DENIED
```

even though User A possesses:

```text
kyc.case.approve
```

---

# 34. Core Dynamic SoD Rule

For sensitive approvals:

```text
CurrentUser != PreviousActor
```

where the previous actor performed a conflicting workflow operation.

Examples:

```text
SubmittedBy != KycApprovedBy
```

```text
KycReviewedBy != ComplianceApprovedBy
```

```text
ComplianceReviewedBy != AccountApprovedBy
```

The exact SoD rules should be associated with the workflow operation rather than implemented as generic UI rules.

---

# 35. SoD Audit Requirements

Every sensitive action should record:

```text
UserId
Role
Action
EntityType
EntityId
WorkflowId
CorrelationId
Timestamp
Result
```

For approval actions, the audit trail must allow an auditor to determine:

```text
Who submitted?
Who reviewed?
Who approved?
When?
For which entity?
Under which workflow?
```

---

# 36. Workflow State Transition Authorization

State transitions must be explicit.

Example:

```text
DRAFT
  └── Submit ──► SUBMITTED

SUBMITTED
  └── Start KYC ──► KYC_IN_PROGRESS

KYC_IN_PROGRESS
  └── KYC complete ──► KYC_COMPLETED

KYC_COMPLETED
  └── Start compliance ──► COMPLIANCE_IN_PROGRESS

COMPLIANCE_IN_PROGRESS
  └── Compliance approved ──► COMPLIANCE_COMPLETED

COMPLIANCE_COMPLETED
  └── Start account opening ──► ACCOUNT_OPENING_IN_PROGRESS

ACCOUNT_OPENING_IN_PROGRESS
  └── Account opened ──► COMPLETED
```

Invalid transitions must be rejected server-side.

---

# 37. Compensation Authorization

Compensation is not equivalent to business approval.

An Operations Administrator may have:

```text
workflow.retry
workflow.reprocess
```

but should not thereby receive:

```text
account.application.approve
```

Compensation actions must be:

- explicitly authorized;
- auditable;
- idempotent where possible;
- restricted to eligible workflow states.

---

# 38. M2M Authorization

Human-user tokens and machine-to-machine tokens represent different security contexts.

Typical human flow:

```text
Browser
   ↓
BFF
   ↓
Business API
```

The BFF operates on behalf of an authenticated user.

Service-to-service flow:

```text
Service A
   ↓
M2M access token
   ↓
Service B
```

M2M authentication establishes:

```text
Which service is calling?
```

It does not automatically answer:

```text
Which business operation is permitted?
```

The receiving service must still authorize the requested operation.

---

# 39. Delegated User Context

Where a downstream operation genuinely needs to preserve the identity of the initiating human user, the architecture may later demonstrate delegated identity or token exchange.

The distinction is:

```text
M2M identity:
"Customer Service BFF is calling."

Delegated user identity:
"Customer Service BFF is calling on behalf of user EMP-10042."
```

The PoC should introduce token exchange only where it demonstrates a real authorization requirement rather than adding it solely for complexity.

---

# 40. Authorization Decision Model

The overall authorization model can be represented as:

```text
                   ┌───────────────┐
                   │ Authenticated │
                   │     User      │
                   └───────┬───────┘
                           │
                           ▼
                    ┌─────────────┐
                    │    RBAC     │
                    │ Role/Perm.  │
                    └──────┬──────┘
                           │
                           ▼
                    ┌─────────────┐
                    │    ABAC     │
                    │ Attributes  │
                    └──────┬──────┘
                           │
                           ▼
                    ┌─────────────┐
                    │    ReBAC    │
                    │ Relationship│
                    └──────┬──────┘
                           │
                           ▼
                    ┌─────────────┐
                    │  Workflow   │
                    │    State    │
                    └──────┬──────┘
                           │
                           ▼
                    ┌─────────────┐
                    │     SoD     │
                    │   Checks    │
                    └──────┬──────┘
                           │
                           ▼
                  ┌──────────────────┐
                  │ Authorization    │
                  │ Decision         │
                  └──────────────────┘
```

---

# 41. Authorization Decision Examples

## Example A — Allowed KYC Approval

```text
User:
  Role = kyc_officer
  Permission = kyc.case.approve
  Department = KYC
  Branch = BLR001
  Clearance = 3

KYC Case:
  Branch = BLR001
  Status = AWAITING_REVIEW
  AssignedOfficer = user

SoD:
  User did not perform conflicting previous action
```

Result:

```text
ALLOW
```

---

## Example B — Branch Restriction

```text
User.Branch = MUM001
KycCase.Branch = BLR001
```

If cross-branch access is not authorized:

```text
DENY
```

---

## Example C — Insufficient Clearance

```text
Required Clearance = 3
User Clearance = 2
```

Result:

```text
DENY
```

---

## Example D — Dynamic SoD Violation

```text
User A performed KYC verification.

User A has kyc.case.approve.

User A attempts to approve the same case.
```

Result:

```text
DENY
Reason: Separation of Duties violation
```

---

## Example E — Operations Administrator

```text
User:
  Role = operations_administrator
  Permission = workflow.retry

Action:
  Retry failed workflow
```

Result:

```text
ALLOW
```

But:

```text
User:
  Role = operations_administrator

Action:
  Approve KYC case
```

Result:

```text
DENY
```

unless a separate explicit business role/permission has been granted.

---

# 42. Authorization Failure Semantics

Authorization failures should be handled consistently.

The API should distinguish, where appropriate:

```text
401 Unauthorized
```

for an unauthenticated request, and:

```text
403 Forbidden
```

for an authenticated user who is not authorized to perform the operation.

The response should not unnecessarily disclose internal authorization policy details.

For example, a client generally does not need to know the complete internal ABAC or SoD evaluation.

Detailed authorization information may instead be recorded in secure structured logs and audit records according to policy.

---

# 43. UI Requirements

The UI may use authorization information to improve the user experience.

For example:

```text
Approve
Reject
Hold
```

buttons may be displayed only when the current user is expected to be able to perform the operation.

However:

> **Hiding a button is not an authorization mechanism.**

Every sensitive operation must be authorized by the server.

Therefore:

```text
UI
 ↓
BFF
 ↓
API
 ↓
Authorization
 ↓
Business operation
```

must remain secure even if a user manually invokes an API endpoint.

---

# 44. Audit and Traceability

Every significant business authorization decision should be traceable through:

```text
UserId
Role
Permission
EntityId
WorkflowId
CorrelationId
TraceId
Action
Result
Timestamp
```

The V3 architecture should propagate correlation information across:

```text
Browser
 ↓
BFF
 ↓
API
 ↓
Database
 ↓
Outbox
 ↓
Kafka
 ↓
Consumer
 ↓
Downstream API
 ↓
SignalR
```

This enables investigation of distributed workflow behavior.

---

# 45. Relationship to the IDP Database

The IDP's `IdentityAccessDb` should contain authorization information such as:

```text
Users
Roles
Permissions
UserRoles
RolePermissions
Branches
Departments
UserEmploymentProfiles
UserRelationships
```

It should not contain the master business data for:

```text
Customers
KYC Cases
Accounts
Payments
```

Those remain owned by their respective bounded contexts.

---

# 46. Initial Demonstration Users

The PoC should contain at least one user for each primary persona.

Suggested users:

| Username | Persona | Role |
|---|---|---|
| `customer.demo` | Customer | `customer` |
| `ananya.cs` | Customer Service Agent | `customer_service_agent` |
| `rahul.kyc` | KYC Officer | `kyc_officer` |
| `meera.compliance` | Compliance Officer | `compliance_officer` |
| `arjun.accounts` | Account Officer | `account_officer` |
| `priya.payments` | Payments Officer | `payments_officer` |
| `vikram.ops` | Operations Administrator | `operations_administrator` |
| `sanjay.audit` | Auditor | `auditor` |
| `platform.admin` | Platform Administrator | `platform_administrator` |

These are demonstration identities only and must not represent real people.

---

# 47. Suggested Organizational Attributes for Demonstration

Example employee attributes:

| User | Department | Branch | Region | Employment Type | Clearance |
|---|---|---|---|---|---:|
| `ananya.cs` | CUSTOMER_SERVICE | BLR001 | SOUTH | FULL_TIME | 2 |
| `rahul.kyc` | KYC | BLR001 | SOUTH | FULL_TIME | 3 |
| `meera.compliance` | COMPLIANCE | BLR001 | SOUTH | FULL_TIME | 4 |
| `arjun.accounts` | ACCOUNTS | BLR002 | SOUTH | FULL_TIME | 3 |
| `priya.payments` | PAYMENTS | BLR002 | SOUTH | FULL_TIME | 4 |
| `vikram.ops` | OPERATIONS | BLR001 | SOUTH | FULL_TIME | 4 |
| `sanjay.audit` | AUDIT | BLR001 | SOUTH | FULL_TIME | 5 |
| `platform.admin` | IT | BLR001 | SOUTH | FULL_TIME | 5 |

The values are illustrative PoC data.

The differing branches, departments, and clearance levels are intentional so that ABAC scenarios can be demonstrated.

---

# 48. Suggested ReBAC Demonstration Relationships

The initial demonstration dataset should contain relationships such as:

```text
ananya.cs
    └── manages ──► Customer CUST-10045

rahul.kyc
    └── assigned_to ──► KYC-10045

meera.compliance
    └── assigned_to ──► KYC-10045

arjun.accounts
    └── assigned_to ──► Account Application ACCAPP-10045
```

The actual resource identifiers are owned by the corresponding microservices.

---

# 49. Authorization Test Scenarios

The V3 PoC should explicitly demonstrate both positive and negative authorization scenarios.

## RBAC

- Customer can create own onboarding.
- Customer Service Agent can manage onboarding.
- KYC Officer can perform KYC operations.
- Compliance Officer can perform compliance operations.
- Account Officer can approve account opening.
- Payments Officer can approve eligible payments.
- Auditor cannot modify business data.

## ABAC

- KYC Officer can access cases within permitted branch.
- KYC Officer cannot access restricted cross-branch cases.
- High-risk case requires sufficient clearance.
- Payment approval threshold requires appropriate authorization level.

## ReBAC

- Customer can access owned account.
- Customer cannot access another customer's account.
- Assigned KYC Officer can access assigned case.
- Unassigned KYC Officer is denied where assignment is required.

## SoD

- User who performed a conflicting action cannot perform the approval.
- Customer cannot approve own onboarding.
- Operational retry does not grant business approval authority.

## Workflow

- KYC cannot be approved before review-ready state.
- Account cannot be opened before mandatory compliance completion.
- Payment cannot be approved after it has already completed.
- Invalid state transitions are rejected.

---

# 50. Guiding Principle

The Enterprise Web Platform V3 authorization model follows this principle:

> **A user should be authorized based not merely on who they are, but on what they are responsible for, what they are permitted to do, their relationship to the business resource, the attributes governing their access, and the current state of the business process.**

The resulting authorization model is therefore:

```text
Identity
   +
RBAC
   +
Permissions
   +
ABAC
   +
ReBAC
   +
Workflow Authorization
   +
Separation of Duties
   +
Auditability
```

This provides the business foundation for the V3 PoC's enterprise security architecture.
