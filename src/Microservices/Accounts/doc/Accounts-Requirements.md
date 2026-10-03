# Accounts — Bounded Context Requirements

**Bounded context:** Accounts  
**Subdomain type:** Core (simplified — not a core-banking ledger)  
**Status:** Planned — `API` and `BFF.Web` folders exist but contain no implementation yet

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Accounts opens and manages customer accounts once onboarding prerequisites are complete. It is the last step of the onboarding workflow and a participant in the Payments saga (funds reservation and release).

| Owns | Does not own |
|---|---|
| Account-opening applications | Onboarding applications (Customer Onboarding) |
| Accounts, account holders and the account lifecycle | KYC and compliance outcomes — it receives them as events |
| Account ownership relationships (`Customer ──owns──► Account`) | Payment instructions (Payments) |
| Funds reservations used by the Payments saga (simplified balances) | A real ledger, interest or statements (out of scope) |

### Planned deployable components

| Component | Location | Technology |
|---|---|---|
| Accounts MFE | `BFF.Web/client-app` | Next.js static export |
| Accounts BFF | `BFF.Web` | ASP.NET Core 10 |
| Accounts API | `API` | ASP.NET Core 10, EF Core, PostgreSQL |
| Database | `EwpAccountsDb` | PostgreSQL |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Account Officer | Review account-opening requests; verify that prerequisites have completed; approve, reject or hold opening; view the account lifecycle | Open an account whose KYC or compliance prerequisites failed; override a compliance rejection without an authorized exception process; approve their own request |
| Customer | View their own accounts | View anyone else's accounts |
| Auditor | View account history | Change anything |

---

## 3. Permissions

| Permission | Account Officer | Customer | Auditor |
|---|:-:|:-:|:-:|
| `account.application.view` / `.review` / `.approve` / `.reject` / `.hold` | ✓ | | |
| `account.lifecycle.view` | ✓ | | |
| `customer.account.view_own` | | ✓ | |
| `account.history.view` | | | ✓ |

API scopes: `accounts.read`, `accounts.write`.

---

## 4. Domain Model (target)

- **`AccountApplication`** aggregate: one per completed-compliance onboarding application.
- **`Account`** aggregate: account number, holder references (customer numbers), product type, status, and a simplified available balance used for reservations.

### States

Account application:

```text
CREATED ──► PENDING_REVIEW ──approve──► APPROVED ──► OPENING ──► OPENED (terminal)
                           ──reject───► REJECTED (terminal)
                                         OPENING ──failure──► FAILED
```

Account: `ACTIVE`, `FROZEN`, `CLOSED`.

---

## 5. Business Rules

1. An account application is created only after `compliance.case.approved` for the same onboarding application.
2. Approval requires KYC and compliance completion **and** the application to be PENDING_REVIEW.
3. **SoD:** the account approver must not be the compliance approver or the workflow initiator for the same onboarding.
4. A funds reservation is idempotent per payment saga ID. Releasing an unknown or already-released reservation is a no-op that succeeds.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| Approve opening | `account_officer` + `account.application.approve` + prerequisites complete + PENDING_REVIEW + SoD (+ `assigned_to` where used) |
| Customer views account | `customer` + `customer.account.view_own` + `owns` the account |
| Reserve / release funds | M2M only: the Payments orchestrator's pinned client + a narrow scope |

---

## 7. Integration (planned)

| Direction | Event / call |
|---|---|
| In | `compliance.case.approved` → create the account application |
| Out | `accounts.account.opened`, `accounts.account.opening.failed` |
| Payments saga | Reserve-funds and release-funds commands with their results. Defined in [Payments-Requirements.md](../../Payments/doc/Payments-Requirements.md) and the [Saga plan](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md). |

---

## 8. Reserved Topology

- Local URLs (reserved): BFF `https://accounts.dev.localhost:45456`, API `https://accounts-api.dev.localhost:48486`. The Shell menu seed uses the same BFF URL.
