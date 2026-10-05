# Payments — Bounded Context Requirements

**Bounded context:** Payments  
**Subdomain type:** Core  
**Status:** Planned — `API` and `BFF.Web` folders exist but contain no implementation yet

Platform-wide rules are not repeated here. See [doc/](../../../../doc/). Payments is the **orchestrated-saga** demonstration; the orchestration pattern itself is described in the [Saga plan](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md).

---

## 1. Purpose and Boundary

Payments accepts payment instructions, validates them, obtains any required approval and executes them, coordinating with Accounts through an explicit saga orchestrator.

| Owns | Does not own |
|---|---|
| Payment instructions and their lifecycle | Account balances and reservations (Accounts) |
| Beneficiaries | Customer data (Customer Onboarding) |
| Payment attempts and processing state | |
| The Payment saga orchestrator and its persisted saga state | |

### Planned deployable components

| Component | Location | Technology |
|---|---|---|
| Payments MFE | `BFF.Web/client-app` | Next.js |
| Payments BFF | `BFF.Web` | NestJS |
| Payments API | `API` | ASP.NET Core 10, EF Core, PostgreSQL |
| Payment saga orchestrator | Inside the Payments context | Persisted state machine |
| Database | `EwpPaymentsDb` | PostgreSQL |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Customer | Initiate eligible payments; view their own payments | Approve payments; view others' payments |
| Payments Officer | Review, validate, approve, reject, hold and release payments; review processing failures; retry eligible processing | Approve a payment they initiated; approve beyond their authorized amount |
| Compliance Officer | Review exception and high-risk payments (via Compliance) | Approve the payment itself |
| Auditor | View payment history | Change anything |

---

## 3. Permissions

| Permission | Customer | Payments Officer | Auditor |
|---|:-:|:-:|:-:|
| `customer.payment.create` / `customer.payment.view_own` | ✓ | | |
| `payment.view` / `.validate` / `.approve` / `.reject` / `.hold` / `.release` / `.retry` | | ✓ | |
| `payment.history.view` | | | ✓ |

API scopes: `payments.read`, `payments.write`.

---

## 4. Domain Model (target)

- **`Payment`** aggregate: payer account, beneficiary, amount, currency, risk classification, status.
- **`Beneficiary`** aggregate: owned by a customer.
- **Payment saga state:** owned by this context's orchestrator. Its fields, steps, compensation and compensation-failure handling are specified in the [Saga plan §2](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments).

### Payment states

```text
INITIATED ──► VALIDATING ──► PENDING_APPROVAL ──approve──► APPROVED ──► PROCESSING ──► COMPLETED
                                              ──reject───► REJECTED
Any active state ──► ON_HOLD ──release──► previous state
PROCESSING ──failure──► FAILED
INITIATED / PENDING_APPROVAL ──cancel──► CANCELLED
```

---

## 5. Business Rules

1. **Approval tiers.** The thresholds are configuration, never UI constants:

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
| Approve | `payments_officer` + `payment.approve` + PENDING_APPROVAL + amount within the user's limit + branch / organizational scope + SoD |
| Customer view | `customer.payment.view_own` + `owns` the payment |

---

## 7. Integration (planned)

Payments commands and events (`payments.*`) and the Accounts reservation commands will be added to the [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md) when they are designed.

---

## 8. Reserved Topology

- Local URLs (reserved): BFF `https://payments.dev.localhost:46388`, API `https://payments-api.dev.localhost:44488`. The Shell menu seed uses the same BFF URL.
