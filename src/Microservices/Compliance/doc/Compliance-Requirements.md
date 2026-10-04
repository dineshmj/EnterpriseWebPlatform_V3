# Compliance — Bounded Context Requirements

**Bounded context:** Compliance  
**Subdomain type:** Core  
**Status:** Backend present (increment 2a: API, case-opening subscriber, screening provider simulator). Officer UI (BFF + MFE) planned for increment 2b.

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Compliance makes the **financial-crime and regulatory decision** on an onboarding application after KYC has verified the customer's identity and evidence. It is a separate bounded context from Customer KYC because it has a different responsible persona, different rules, different data (screening and risk) and its own approval authority.

| Owns | Does not own |
|---|---|
| Compliance cases | Identity and document verification (Customer KYC) |
| AML / sanctions / PEP screening results (from a simulated external provider) | The customer profile (Customer Onboarding) |
| Risk assessment and risk rating | Account opening (Accounts) |
| Compliance decisions and holds | |
| Compliance case assignment (ReBAC data) | |

### Deployable components

| Component | Location | Technology | Status |
|---|---|---|---|
| Compliance MFE | `BFF.Web/client-app` | Next.js static export | Planned (2b) |
| Compliance BFF | `BFF.Web` | ASP.NET Core 10 (Duende BFF) | Planned (2b) |
| Compliance API | [API](../API/) — `https://compliance-api.dev.localhost:44306` | ASP.NET Core 10 REST, EF Core, PostgreSQL; in-process Outbox relay and screening worker | Present |
| ComplianceCaseOpeningSubscriber | [Subscribers/Compliance](../../../AsyncWorkflows/Subscribers/Compliance/ComplianceCaseOpeningSubscriber/) | .NET worker on the shared reliable subscriber pipeline; consumes `kyc.case.approved` | Present |
| Database | `EwpComplianceDb` — [EwpComplianceDb.sql](../API/ComplianceDb/EwpComplianceDb.sql) | PostgreSQL; service user `ewp_compliance_api` | Present |
| External provider | [Screening Provider Simulator](../../../Simulators/ScreeningProviderSimulator/) — `https://localhost:44366` | Switchable Healthy / Slow / Failing / Down, to demonstrate timeout, retry and circuit breaker | Present |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Compliance Officer | Review KYC outcomes, AML screening and risk ratings; claim and release cases; approve or reject; place on or release from compliance hold | Bypass mandatory KYC; alter results produced by verification or screening services; decide a case they initiated or verified in KYC; act outside their branch; approve above their clearance |
| Auditor | View compliance history and decisions | Change anything |

---

## 3. Permissions

`compliance.case.view`, `compliance.case.review`, `compliance.aml.review`, `compliance.risk.assess`, `compliance.case.request_information`, `compliance.case.approve`, `compliance.case.reject`, `compliance.case.hold`, `compliance.case.release` — all held by `compliance_officer`.

API scopes: `compliance.read`, `compliance.write` (API resource `compliance-api`). The case-opening subscriber uses its own M2M client, pinned by client ID to the single internal endpoint.

---

## 4. Domain Model

- **`ComplianceCase`** aggregate: one per onboarding application (unique `ApplicationRef`). It references the application, the customer and the KYC case by value, and holds the branch, the workflow initiator and both KYC stage deciders (for SoD), the screening result and attempts, the risk rating and required clearance, the assignee, the hold and the decision.
- **`ScreeningOutcome`**: `CLEAR`, `POTENTIAL_MATCH`, `MATCH`, with the provider, its reference and the screened-at time.
- **`RiskRating`** (`RiskPolicy`): CLEAR → `LOW` (approval needs clearance 3), POTENTIAL_MATCH → `MEDIUM` (4), MATCH → `HIGH` (5).

### States

```text
SCREENING ──result──► UNDER_REVIEW ──approve──► APPROVED (terminal)
   ▲   │                   │        ──reject───► REJECTED (terminal)
   └───┘ provider failure  └──hold──► ON_HOLD ──release-hold──► UNDER_REVIEW
         (retry, back-off)            (reject is also allowed from ON_HOLD)
```

`AWAITING_INFORMATION` (request more information) is not implemented yet; a hold with a reason covers it for now.

---

## 5. Business Rules

1. A compliance case is created only for an application whose KYC case is APPROVED (from `kyc.case.approved`), at most once per application.
2. Approval is not possible while screening is incomplete or the case is on hold.
3. **High-risk cases** require a higher clearance level to approve (ABAC; see `RiskPolicy`). Any compliance officer may reject.
4. **SoD across contexts:** the compliance officer must not be the workflow initiator nor either KYC stage decider of the same application. If the initiator is unknown the rule fails closed.
5. A provider failure is a **technical** failure. It is never a silent pass: the case stays in SCREENING and is retried with exponential back-off (15 s doubling to 5 min). Calls to the provider are protected by timeouts, retries and a circuit breaker, and readiness reports Degraded once a case has waited more than 2 minutes.
6. **ReBAC:** the first officer action (claim, or a decision) assigns the case. Afterwards only the assigned officer may act until they release it.
7. Reject needs remarks; hold needs a reason. A decided case is final.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View | `compliance.read` scope + `compliance.case.view` + `compliance_officer` + department `COMPLIANCE` + clearance ≥ 3; own branch only (a case of another branch is 404) |
| Claim / release | View rules + `compliance.write` + `compliance.case.review` + SoD; release by the assignee only |
| Approve | `compliance.case.approve` + the above + clearance ≥ the case's required clearance (enforced in the aggregate) + assigned to the officer or unassigned + status UNDER_REVIEW + SoD |
| Reject | `compliance.case.reject` + assignment + SoD + remarks |
| Hold / release hold | `compliance.case.hold` / `compliance.case.release` + assignment + SoD + state eligibility |

---

## 7. Integration

| Direction | Contract |
|---|---|
| In | `kyc.case.approved` → `ComplianceCaseOpeningSubscriber` → `POST internal/v1/compliance/cases/from-kyc-approved` (Inbox, plus one case per `ApplicationRef`) |
| Out | `compliance.case.created`, `compliance.case.approved`, `compliance.case.rejected` (Outbox) → `OnboardingOutcomeSubscriber` → Customer Onboarding (COMPLIANCE_IN_PROGRESS / COMPLIANCE_COMPLETED / COMPLIANCE_REJECTED) |
| Out (synchronous) | `POST /v1/screenings` to the screening provider (API key; resilience pipeline) |

Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

---

## 8. Topology and Demo Data

- Compliance BFF / MFE URL (reserved for 2b): `https://compliance.dev.localhost:44399`. The Shell menu seed registers a **Compliance** microservice there, owning the "Compliance Monitor" item (`/v1/compliance/view-all`).
- Demo officer: `olivia.compliance` (SYD001, clearance 4) may approve LOW and MEDIUM cases. A HIGH case needs clearance 5; a senior officer arrives in 2c. Case assignment is held by Compliance itself, not by IDP seed data.
