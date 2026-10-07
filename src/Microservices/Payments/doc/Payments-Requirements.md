# Payments — Bounded Context Requirements

**Bounded context:** Payments  
**Subdomain type:** Core  
**Status:** Present: the Payments API with the saga orchestrator, Accounts funds holds, payment network simulator and two courier workers (5a); the Payments BFF and MFE - new payment, status page with the live timeline, payments list (5b-1); human approval by a payments officer and notifications (5b-2). The operations "Retry release" follows in 5c.

Platform-wide rules are not repeated here. See [doc/](../../../../doc/). Payments is the **orchestrated-saga** demonstration; the orchestration pattern itself is described in the [Saga plan](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md).

---

## 1. Purpose and Boundary

Payments accepts payment instructions captured by bank staff for a customer (an **assisted channel**, not internet banking), validates them, obtains any required approval and executes them, coordinating with Accounts and the external payment network through an explicit saga orchestrator.

| Owns | Does not own |
|---|---|
| Payment instructions and their lifecycle | Account balances and reservations (Accounts) |
| Beneficiaries | Customer data (Customer Onboarding) |
| Payment attempts and processing state | |
| The Payment saga orchestrator and its persisted saga state | |

### Deployable components

| Component | Location | Technology | Status |
|---|---|---|---|
| Payments MFE | [BFF.Web/client-app](../BFF.Web/README.md) — new payment, status page, payments list, approval queue | Next.js static export | Present |
| Payments BFF | [BFF.Web](../BFF.Web/README.md) — `https://payments.dev.localhost:46388` | ASP.NET Core 10 + Duende BFF | Present |
| Payments API | `API` | ASP.NET Core 10, EF Core, PostgreSQL | Present |
| Payment saga orchestrator | `PaymentSaga` inside the Payments API, with its step runner | Persisted state machine | Present |
| PaymentsSagaReplySubscriber | `src/AsyncWorkflows/Subscribers/Payments` | .NET worker (courier) | Present |
| Database | `EwpPaymentsDb` | PostgreSQL | Present |

Accounts adds funds holds and the `AccountsCommandSubscriber` courier; the Payment Network Simulator (`src/Simulators/PaymentNetworkSimulator`) stands in for the external NPP-style network.

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Customer Service Agent | Capture a payment for a customer at the counter or on the phone (assisted channel); follow it | Approve payments; see another branch's payments |
| Customer | (Self-service: later) Initiate eligible payments; view their own payments | Approve payments; view others' payments |
| Payments Officer | Review, validate, approve, reject, hold and release payments; review processing failures; retry eligible processing | Approve a payment they initiated; approve beyond their authorized amount |
| Compliance Officer | Review exception and high-risk payments (via Compliance) | Approve the payment itself |
| Auditor | View payment history | Change anything |

---

## 3. Permissions

| Permission | Customer | Customer Service Agent | Payments Officer | Auditor |
|---|:-:|:-:|:-:|:-:|
| `customer.payment.create` / `customer.payment.view_own` | ✓ | | | |
| `payment.initiate` | | ✓ | | |
| `payment.view` | | ✓ | ✓ | |
| `payment.validate` / `.approve` / `.reject` / `.hold` / `.release` / `.retry` | | | ✓ | |
| `payment.history.view` | | | | ✓ |

API scopes: `payments.read`, `payments.write`.

---

## 4. Domain Model (target)

- **`Payment`** aggregate: payer account, beneficiary, amount, currency, risk classification, status.
- **`Beneficiary`** aggregate: owned by a customer.
- **Payment saga state:** owned by this context's orchestrator. Its fields, steps, compensation and compensation-failure handling are specified in the [Saga plan §2](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments).

### Payment states (implemented)

```text
INITIATED → RESERVING_FUNDS → (PENDING_APPROVAL) → SENDING_TO_NETWORK → SETTLING_FUNDS → COMPLETED
RESERVING_FUNDS ✗ (no funds)                  → REJECTED                (nothing reserved, nothing to undo)
after the reservation ✗ (network, approver)  → COMPENSATING → FAILED / REJECTED (funds released)
COMPENSATING ✗✗✗ (release not confirmed)     → COMPENSATION_FAILED     (operations retry the release)
```

Approve / reject from PENDING_APPROVAL: present (5b-2). Planned: hold and release, cancel before sending.

---

## 5. Business Rules

1. **Approval tiers.** The thresholds are configuration, never UI constants (`Payments:ApprovalThreshold`, default 1,000.00 AUD; above it a payments officer approves):

   | Tier | Path |
   |---|---|
   | Low value | Validation → processing |
   | High value | Validation → Payments Officer approval → processing |
   | Exception / high risk | Validation → compliance review → Payments Officer approval → processing |

2. The required approval level depends on the amount and risk (ABAC on clearance level).
3. **SoD:** payment initiator ≠ payment approver.
4. **Idempotency:** every command carries an idempotency key, so a retry never creates a second payment attempt.
5. A completed payment cannot be approved, rejected or retried.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| Initiate | `payments.write` + `payment.initiate` + a branch; the payment belongs to the staff member's branch; the request's `Idempotency-Key` belongs to the person who used it first |
| View | `payments.read` + `payment.view` or `payment.initiate`; own branch only (another branch's payment is 404) |
| Approve | `payments.write` + `payment.approve` + own branch + PENDING_APPROVAL + **not the initiator** (SoD) + amount within the officer's clearance limit (`Payments:ApprovalLimits`: clearance 3 → 10,000; 4 → 100,000; 5 → any) |
| Reject | `payments.write` + `payment.reject` + own branch + PENDING_APPROVAL + not the initiator; remarks required; the saga releases the reserved funds |
| Funds | Accounts reserves only on an ACTIVE account of the customer named in the payment, in AUD, within the available balance |
| Approve | `payments_officer` + `payment.approve` + PENDING_APPROVAL + amount within the user's limit + branch / organizational scope + SoD |
| Customer view | `customer.payment.view_own` + `owns` the payment |

---

## 7. Integration

Commands to Accounts (`accounts.commands`), Accounts' replies (`accounts.funds.replies`) and the payments' outcomes (`payments.payment.events`): [Integration-Event-Catalogue.md §4.5](../../../../doc/Integration-Event-Catalogue.md). The payment network is called directly over HTTP with the PaymentRef as Idempotency-Key.

---

## 8. Reserved Topology

- Local URLs: API `https://payments-api.dev.localhost:44488`; Payment Network Simulator `https://localhost:46386`; BFF `https://payments.dev.localhost:46388` - the Shell menu seed uses the same BFF URL ("New Payment" and "View Payments" for customer service agents).
- Workers: AccountsCommandSubscriber (health 5108), PaymentsSagaReplySubscriber (health 5109).