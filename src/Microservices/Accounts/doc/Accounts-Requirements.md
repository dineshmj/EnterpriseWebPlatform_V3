# Accounts — Bounded Context Requirements

**Bounded context:** Accounts (ACC)  
**Subdomain type:** Core (simplified — not a core-banking ledger)  
**Status:** Present: API, account-application opening subscriber and core-banking simulator (3a); officer UI, BFF + MFE (3b); compensation of a failed opening (3c).

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Accounts opens customer accounts once onboarding prerequisites are complete. It is the **last step of the onboarding saga**: when its account is opened, the onboarding is COMPLETED. It is also a planned participant in the Payments saga (funds reservation and release).

| Owns | Does not own |
|---|---|
| Account applications (one per onboarding application that Compliance approved) | Onboarding applications (Customer Onboarding) |
| Accounts, their holder and lifecycle (`Customer ──owns──► Account`) | KYC and compliance outcomes — it receives them as events |
| The officer's opening decision and the account product | The account itself in the system of record: the **core-banking system** opens it and issues the BSB and account number |
| Funds reservations for the Payments saga (planned, simplified) | A real ledger, interest or statements (out of scope) |

### Deployable components

| Component | Location | Technology | Status |
|---|---|---|---|
| Accounts MFE | [BFF.Web/client-app](../BFF.Web/README.md) — work queue, application page, accounts, lifecycle | Next.js static export | Present |
| Accounts BFF | [BFF.Web](../BFF.Web/README.md) — `https://accounts.dev.localhost:45456` | ASP.NET Core 10 (Duende BFF) | Present |
| Accounts API | [API](../API/) — `https://accounts-api.dev.localhost:48486` | ASP.NET Core 10 REST, EF Core, PostgreSQL; in-process Outbox relay and account-opening worker | Present |
| AccountApplicationOpeningSubscriber | [Subscribers/Accounts](../../../AsyncWorkflows/Subscribers/Accounts/AccountApplicationOpeningSubscriber/README.md) | .NET worker on the shared reliable subscriber pipeline; consumes `compliance.case.approved` | Present |
| Database | `EwpAccountsDb` — [EwpAccountsDb.sql](../API/AccountsDb/EwpAccountsDb.sql) | PostgreSQL; service user `ewp_accounts_api` | Present |
| External system | [Core Banking Simulator](../../../Simulators/CoreBankingSimulator/README.md) — `https://localhost:46376` | Switchable Healthy / Slow / Failing / Down / Refusing; idempotent per `Idempotency-Key` | Present |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Account Officer | Review account applications of their branch; claim and release them; approve (choosing the product), reject or hold the opening; view opened accounts | Open an account for an application they initiated, or one they approved in Compliance; act outside their branch; open an account without the core-banking system |
| Customer | View their own accounts (planned) | View anyone else's accounts |
| Auditor | View account history (planned) | Change anything |

---

## 3. Permissions

| Permission | Account Officer | Customer | Auditor |
|---|:-:|:-:|:-:|
| `account.application.view` / `.review` / `.approve` / `.reject` / `.hold` | ✓ | | |
| `account.lifecycle.view` | ✓ | | |
| `customer.account.view_own` | | ✓ | |
| `account.history.view` | | | ✓ |

API scopes: `accounts.read`, `accounts.write` (API resource `accounts-api`; its tokens carry the officer's role, permissions, department, branch and clearance). The opening subscriber uses its own M2M client, pinned by client ID to the single internal endpoint.

---

## 4. Domain Model

- **`AccountApplication`** aggregate: one per onboarding application (unique `ApplicationRef`). It references the application, the customer and the approving Compliance case by value, and holds the branch, the workflow initiator and the Compliance approver (for SoD), the assignee, the hold, the decision (with the decision's command ID, so later events name it as their cause), the product, and the opening attempts, account number or failure reason.
- **`Account`** aggregate: BSB and account number (issued by core banking), holder customer number, branch, product (`EVERYDAY_TRANSACTION`, `SAVINGS`), status `ACTIVE` (`FROZEN` / `CLOSED` planned), core-banking reference. One per onboarding application.

### States

```text
PENDING_REVIEW ──approve──► OPENING ──core banking opened──► OPENED (terminal; account ACTIVE)
   │  ▲        ──reject───► REJECTED (terminal)
hold ▼  │ release              OPENING ──refused, or still failing after N attempts──► FAILED (terminal)
 ON_HOLD ──reject──► REJECTED
```

`APPROVED` is a moment rather than a resting state: approval records the decision and hands the opening to the core-banking system at once.

---

## 5. Business Rules

1. An account application is created only from `compliance.case.approved`, at most once per onboarding application.
2. **SoD across contexts:** the account officer must be neither the workflow initiator nor the Compliance officer who approved the application. If the initiator is unknown the rule fails closed.
3. **ReBAC:** the first officer action (claim, or a decision) assigns the application; afterwards only the assigned officer may act until they release it.
4. Reject needs remarks; hold needs a reason. A decision is final.
5. **Only the core-banking system opens an account.** A technical failure keeps the application OPENING and is retried with exponential back-off (15 s doubling to 5 min). After `MaxOpeningAttempts` (6) failures, or a refusal (HTTP 422), the application is FAILED and `AccountOpeningFailed` is published; Customer Onboarding compensates the onboarding (COMPENSATING → REJECTED, documents invalidated). The background opening continues the trace of the officer's approval (stored as `opening_trace_parent`).
6. **No duplicate accounts:** every core-banking request carries an `Idempotency-Key` (the `ApplicationRef`). A retried request after a lost answer returns the same account, which is why the POST may be retried at all.
7. **The account holder's name** comes from `compliance.case.approved` (the applicant as Compliance cleared them) and is the name core banking opens the account in. Accounts stores no other personal data — no address or contact details.
8. A funds reservation (Payments, planned) is idempotent per payment saga ID; releasing an unknown or already-released reservation is a no-op that succeeds.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View applications | `accounts.read` + `account.application.view` + `account_officer` + department `ACCOUNTS` + clearance ≥ 3; own branch only (another branch's application is 404) |
| Claim / release | View rules + `accounts.write` + `account.application.review` + SoD; release by the assignee only |
| Approve | `account.application.approve` + assigned to the officer (or unassigned) + PENDING_REVIEW + SoD |
| Reject | `account.application.reject` + assignment + SoD + remarks |
| Hold / release hold | `account.application.hold` + assignment + SoD + state eligibility |
| View accounts | `accounts.read` + `account.lifecycle.view` + officer rules; own branch only |
| Customer views account (planned) | `customer` + `customer.account.view_own` + `owns` the account |
| Reserve / release funds (planned) | M2M only: the Payments orchestrator's pinned client + a narrow scope |

---

## 7. Integration

| Direction | Contract |
|---|---|
| In | `compliance.case.approved` → `AccountApplicationOpeningSubscriber` → `POST internal/v1/accounts/applications/from-compliance-approved` (Inbox, plus one application per `ApplicationRef`) |
| Out | `accounts.application.created` → CO: ACCOUNT_OPENING_IN_PROGRESS; `accounts.account.opened` → CO: COMPLETED; `accounts.application.rejected` → CO: REJECTED (+ document invalidation); `accounts.account.opening.failed` → CO: COMPENSATING → REJECTED (+ document invalidation). All via the Outbox and `OnboardingOutcomeSubscriber`. |
| Out (synchronous) | `POST /v1/accounts` to the core-banking system (API key, `Idempotency-Key`; resilience pipeline) |
| Payments saga (planned) | Reserve-funds and release-funds commands with their results. Defined in [Payments-Requirements.md](../../Payments/doc/Payments-Requirements.md) and the [Saga plan](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md). |

Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

---

## 8. Topology and Demo Data

- BFF / MFE: `https://accounts.dev.localhost:45456`. The Shell menu seed registers **View Account Applications** (`/v1/accounts/applications/view-all`), **View Accounts** (`/v1/accounts/view-all`) and **Account Lifecycle** (`/v1/accounts/lifecycle/view-all`, read-only until freeze / close exist) there; the application page is `/v1/accounts/applications/view-details?applicationId=…`.
- Demo officer: `jack.accounts` (SYD001, clearance 3) — in the same branch as the onboarding, KYC and Compliance demo users, so he works Sophie's onboardings.