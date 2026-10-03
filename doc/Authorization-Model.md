# Enterprise Web Platform V3
## Authorization Model

**Document:** Authorization Model  
**Version:** 2.0  
**Domain:** Banking Services  
**Status:** Living document

---

## 1. Purpose

This document defines **how authorization decisions are made** across EWP V3. It is platform-wide and technology-neutral.

| This document owns | Owned elsewhere |
|---|---|
| Authorization principles and the decision pipeline | Persona descriptions and role codes → [Application-Personas.md](Application-Personas.md) |
| Permission naming and platform-level (cross-context) permissions | Context-specific permissions, states and approval rules → each context's requirements document |
| Scopes vs permissions | Claims the IDP issues, demo users, attributes and relationships → [IDP-Requirements.md](../src/IDP/doc/IDP-Requirements.md) |
| ABAC, ReBAC, SoD, workflow-state and notification authorization mechanics | Which rules each service enforces today → the "Implementation status" section of each context's requirements document |
| Human vs service identity in authorization | Event envelope fields → [Integration-Event-Catalogue.md](Integration-Event-Catalogue.md) |

EWP V3 is an architectural PoC, not a production banking platform. The banking domain provides realistic pressure for the authorization patterns described here.

---

## 2. Principles

1. Authentication establishes **who** the caller is; it never by itself authorizes anything.
2. Authorization is **deny-by-default**: every mandatory predicate must succeed.
3. Authorization is enforced **server-side** in the BFF and, authoritatively, in the API that owns the resource. UI visibility and menu visibility are never authorization.
4. **RBAC alone is insufficient.** Roles grant broad capability; attributes, relationships, workflow state and Separation of Duties narrow it.
5. Authorization is evaluated against the **specific resource** being accessed (object-level authorization), not merely the endpoint.
6. **Operational privileges never imply business approval privileges.**
7. **Human identity and service identity are distinct** security contexts and must stay distinct across asynchronous boundaries.
8. The human who **initiated** a workflow is accountability context, **not** an authorization grant.
9. Authorization failures reveal no more than necessary; the detail goes to secure logs and audit.
10. Significant authorization decisions and all business approvals are **auditable**.
11. Workflow **notifications are authorization decisions** too.
12. Authorization **fails closed**: missing attributes, missing relationships or missing initiator data result in denial, never in skipped checks.

---

## 3. Roles and Permissions

### 3.1 Roles

There is one role per persona. The role codes are listed in [Application-Personas.md §2](Application-Personas.md#2-personas).

### 3.2 Permissions

A permission is a single business or operational capability. Roles receive permissions through the role–permission relationship held by the IDP:

```text
User ──► Role ──► Permission
```

Naming convention:

```text
<bounded-context>.<resource>.<action>
```

The suffix `_own` (e.g. `customer.account.view_own`) means the permission is usable **only** together with an ownership relationship (see §6).

Each bounded context's requirements document lists the permissions that belong to it. The permissions below are **platform-level**: they are not owned by any single business context.

| Permission | Purpose | Held by |
|---|---|---|
| `workflow.status.view` | See business workflow status | All staff roles |
| `workflow.view` | See operational workflow detail | `operations_administrator` |
| `workflow.retry`, `workflow.pause`, `workflow.resume`, `workflow.reprocess` | Technical recovery of workflows | `operations_administrator` |
| `integration.status.view`, `consumer.status.view`, `outbox.view`, `inbox.view` | Integration / messaging diagnostics | `operations_administrator` |
| `service.health.view` | Service health | `operations_administrator`, `platform_administrator` |
| `audit.view`, `audit.search`, `workflow.history.view`, `approval.history.view` | Audit and investigation | `auditor` |
| `platform.configuration.view` / `.update`, `platform.user.view` / `.manage`, `platform.role.view` / `.manage`, `platform.permission.view`, `platform.health.view` | Platform administration | `platform_administrator` |

None of these permissions authorizes a business approval.

### 3.3 Scopes vs Permissions

Two different questions must both be answered with "yes":

| Concept | Question it answers | Carried in | Example |
|---|---|---|---|
| **API scope** | May this *client application* call this API for this kind of operation? | `scope` claim of the access token | `customer-kyc.write` |
| **Permission** | May this *user* perform this business operation? | `permission` claims of the user | `kyc.case.approve` |

An API must therefore check the scope **per operation** (a read scope must not pass a write endpoint) **and** the user's permission, attributes, relationships and the resource's state. A scope never substitutes for a permission, and vice versa.

---

## 4. The Decision Pipeline

```text
Authenticated principal
        │
        ▼
Scope (client may call this operation)
        │
        ▼
RBAC (role → permission)
        │
        ▼
ABAC (user, resource and environment attributes)
        │
        ▼
ReBAC (relationship between user and resource)
        │
        ▼
Workflow state (operation valid in the resource's current state)
        │
        ▼
Separation of Duties
        │
        ▼
ALLOW / DENY  ──► audit
```

A generic approval is permitted only when **all** of the following hold:

1. The user is authenticated.
2. The client holds the required scope.
3. The user holds the required permission.
4. ABAC requirements are satisfied.
5. ReBAC requirements are satisfied where the operation requires a relationship.
6. The resource is in an approvable workflow state.
7. All prerequisite steps are complete.
8. Separation of Duties checks succeed.
9. The required approval level is satisfied.
10. The operation has not already been completed.

`InitiatedByUserId` is intentionally **not** a stage in this pipeline (see §9).

---

## 5. ABAC — Attribute-Based Access Control

ABAC narrows a permission using attributes of three kinds:

| Kind | Examples | Owner |
|---|---|---|
| User | `employee_id`, `department`, `branch`, `region`, `employment_type`, `clearance_level` | IDP (issued as claims) |
| Resource | branch of the customer or case, risk level, classification, payment amount, workflow status | The bounded context that owns the resource |
| Environment | request time, channel, authentication strength | Request context |

Rules:

- The IDP owns **user** attributes only. Resource attributes are owned and evaluated by the bounded context that owns the resource; the IDP never holds business master data.
- Thresholds (e.g. a payment approval limit or a minimum clearance) are **configuration or domain rules**, never UI constants.
- Environmental attributes are used selectively, only where they demonstrate a real requirement.

Typical rule shapes:

```text
Branch scope:      user.branch == resource.branch            (unless cross-branch access is granted)
Clearance:         user.clearance_level >= resource.requiredClearance
Amount threshold:  resource.amount <= limitFor(user.clearance_level)
```

---

## 6. ReBAC — Relationship-Based Access Control

ReBAC asks whether the user has the **required relationship** with this particular resource:

```text
RBAC:  "Are you a KYC Officer?"
ABAC:  "Are you a KYC Officer in the right branch with sufficient clearance?"
ReBAC: "Are you the KYC Officer assigned to this case?"
```

Relationship types used in EWP V3: `works_at`, `manages`, `assigned_to`, `owns`, `belongs_to`.

**Ownership of relationship data.** A relationship is a business fact. It is owned by the bounded context that owns the resource (customer assignment by Customer Onboarding, case assignment by KYC or Compliance, account ownership by Accounts). For the PoC, the IDP holds a simplified `user_relationships` store for demonstration. That store is an interim convenience: the target is for each context to own its relationship facts.

---

## 7. Workflow-State Authorization

Holding a permission does not make an operation valid at every point in a workflow. For example, `kyc.case.approve` is meaningful only while the KYC stage is awaiting review.

- Each bounded context defines its states and valid transitions in its own requirements document.
- Invalid transitions are rejected **server-side by the domain model** (aggregate), not by the UI.
- An operation already completed cannot be performed again (idempotent "already decided" responses or a 409 Conflict).

---

## 8. Separation of Duties (SoD)

### 8.1 Static SoD

Certain role combinations may be declared incompatible for the same user (for example `kyc_officer` + `compliance_officer` where complete independence is required). Static SoD is evaluated when roles are assigned.

### 8.2 Dynamic SoD

A user may hold a valid role and still be denied because of **what they have already done in this workflow**:

```text
CurrentUser != PreviousActor (for the conflicting operation)
```

Examples of conflicting pairs:

```text
Workflow initiator     != KYC decision maker
KYC decision maker     != Compliance approver
Compliance approver    != Account-opening approver
Payment initiator      != Payment approver
```

The exact SoD rules of a workflow step are defined in the owning context's requirements document, close to the operation, not as generic UI rules.

### 8.3 SoD must fail closed

If the information needed to evaluate an SoD rule is missing (for example, the initiator is unknown), the decision is **DENY** or escalation to a supervisor — never "skip the check".

---

## 9. Human Initiator, Service Identity and M2M Authorization

### 9.1 Two identities, two purposes

```text
Human workflow initiator  = who originally started the business workflow   (accountability)
Service identity          = which technical principal executes this step   (authentication of the caller)
```

The initiator is captured at the business-transaction / Outbox boundary and travels in the event envelope as `InitiatedByUserId`. It is used for accountability, SoD evaluation and choosing the notification audience. It **never** grants access by itself:

```text
User = workflow initiator, but lacking the required permission  ──►  DENY
```

### 9.2 M2M authorization

M2M (client-credentials) authentication answers *"which service is calling?"*. It does not answer *"is this business operation permitted?"*. The receiving API must still authorize:

```text
Calling client (pinned client_id) + required scope + requested operation + target resource + workflow state
```

An M2M caller must be pinned by `client_id`, hold the narrowest scope that works, and be limited to the specific operations it exists to perform.

### 9.3 Delegated user context

When a downstream service genuinely needs to know **which human** an M2M call is acting for (e.g. Documents Management deciding whether a document may be read), the target pattern is delegated identity, such as OAuth 2.0 Token Exchange (RFC 8693) or a signed actor claim. Unsigned headers asserted by the caller are not a substitute. Token exchange is introduced only where it demonstrates a real authorization requirement.

---

## 10. Object-Level Authorization

Every read or write is evaluated against the specific resource:

```text
Permission + target resource + ownership / relationship + organizational scope + workflow state
```

List endpoints must filter to the resources the caller may see; they must not return everything and rely on the UI to hide rows. This is the primary defense against IDOR / BOLA.

---

## 11. Notification Authorization

Delivering a real-time notification is an authorization decision:

```text
Workflow event ──► candidate audience (initiator, assigned officer, supervisor, authorized operations role)
              ──► authorization policy per recipient
              ──► authenticated notification connection of that recipient only
```

The platform never treats "all connected users" as an implicit audience.

---

## 12. Operational and Compensation Actions

Retry, reprocess, pause, resume and compensation are **operational** actions. They must be explicitly authorized, auditable, idempotent where possible, restricted to eligible workflow states, and must never constitute or imply a business approval.

---

## 13. Failure Semantics

| Situation | Response |
|---|---|
| Not authenticated | `401 Unauthorized` |
| Authenticated but not permitted | `403 Forbidden` |
| Resource exists but caller may not know that | `404 Not Found` (where disclosing existence would leak information) |
| Operation invalid in the current workflow state, or already completed | `409 Conflict` |

Responses do not reveal internal ABAC or SoD evaluation details. Those details are recorded in structured logs and audit records.

---

## 14. Authorization Audit

Every significant authorization decision and every business approval records at least:

```text
UserId, Role, Permission, Action, EntityType, EntityId,
WorkflowId, CorrelationId, TraceId, Timestamp, Result (and SoD rule evaluated, where applicable)
```

so that an auditor can answer: who submitted, who reviewed, who approved, when, for which entity, under which workflow, and which service identity performed any technical step.

---

## 15. Authorization Test Categories

Every bounded context must demonstrate both positive and negative cases in each category below. The concrete scenarios live in the context's requirements document.

| Category | Must demonstrate |
|---|---|
| Scope | A read-only token is rejected by a write operation |
| RBAC | A role without the permission is denied |
| ABAC | Cross-branch and insufficient-clearance access is denied |
| ReBAC | An unrelated or unassigned user is denied; an owner or assignee is allowed |
| Workflow state | An operation in the wrong state is rejected |
| SoD | The conflicting previous actor is denied; missing SoD data fails closed |
| Operational vs business | `operations_administrator` and `platform_administrator` cannot approve |
| Initiator | Being the initiator grants nothing |
| Object level | List endpoints return only resources the caller may see |
