# Compliance — Bounded Context Requirements

**Bounded context:** Compliance  
**Subdomain type:** Core  
**Status:** Planned — no code yet. This folder holds requirements only.

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Compliance makes the **financial-crime and regulatory decision** on an onboarding application after KYC has verified the customer's identity and evidence. It is a separate bounded context from Customer KYC because it has a different responsible persona, different rules, different data (screening and risk) and its own approval authority.

| Owns | Does not own |
|---|---|
| Compliance cases | Identity and document verification (Customer KYC) |
| AML / sanctions / PEP screening results (from simulated external providers) | The customer profile (Customer Onboarding) |
| Risk assessment and risk rating | Account opening (Accounts) |
| Compliance decisions, holds and escalations | |
| Compliance case assignment (ReBAC data) | |

### Planned deployable components

| Component | Location | Technology |
|---|---|---|
| Compliance MFE | `BFF.Web/client-app` | Next.js static export |
| Compliance BFF | `BFF.Web` | To be chosen (ASP.NET Core or NestJS) |
| Compliance API | `API` | ASP.NET Core 10, EF Core, PostgreSQL |
| Compliance subscriber | `API` or a context-owned worker | Consumes `kyc.case.approved` |
| Database | `EwpComplianceDb` | PostgreSQL |
| External providers | Simulated AML and screening providers | Used to demonstrate timeout, retry and circuit-breaker behaviour |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Compliance Officer | Review KYC outcomes, AML screening and risk assessments; request more information; approve or reject; place on or release from compliance hold; escalate high-risk cases | Bypass mandatory KYC; alter results produced by verification or screening services; approve their own decision where independent approval is required; act outside their organizational scope |
| Auditor | View compliance history and decisions | Change anything |

---

## 3. Permissions

`compliance.case.view`, `compliance.case.review`, `compliance.aml.review`, `compliance.risk.assess`, `compliance.case.request_information`, `compliance.case.approve`, `compliance.case.reject`, `compliance.case.hold`, `compliance.case.release` — all held by `compliance_officer`.

API scopes: to be registered (`compliance.read`, `compliance.write`).

---

## 4. Domain Model (target)

- **`ComplianceCase`** aggregate: one per onboarding application. It references the application number and the KYC case ID by value, and holds the screening results, the risk rating, the decision and the assignee.
- **`ScreeningResult`** value object: provider, outcome (`CLEAR`, `POTENTIAL_MATCH`, `MATCH`), checked-at time.
- **`RiskRating`** value object: `LOW`, `MEDIUM`, `HIGH`, with the rationale.

### States

```text
CREATED ──► SCREENING ──► UNDER_REVIEW ──approve──► APPROVED (terminal)
                              │        ──reject───► REJECTED (terminal)
                              ├──► AWAITING_INFORMATION ──► UNDER_REVIEW
                              └──► ON_HOLD ──release──► UNDER_REVIEW
```

---

## 5. Business Rules

1. A compliance case is created only for an application whose KYC case is APPROVED.
2. Approval is not possible while mandatory screening or risk assessment is incomplete.
3. **High-risk cases** require a higher clearance level to approve (ABAC).
4. **SoD:** the compliance approver must not be the KYC decision maker of the same application, nor the workflow initiator.
5. A provider failure is a **technical** failure (retry, then a recovery state). It is never a silent pass.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| Approve | `compliance_officer` + `compliance.case.approve` + department `COMPLIANCE` + clearance ≥ the case's required clearance + `assigned_to` the case (where assignment is used) + status UNDER_REVIEW + screening and risk complete + SoD |
| Hold / release | `compliance.case.hold` / `compliance.case.release` + state eligibility |

---

## 7. Integration (planned)

| Direction | Event |
|---|---|
| In | `kyc.case.approved` → create a compliance case |
| Out | `compliance.case.approved`, `compliance.case.rejected` |

Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

---

## 8. Reserved Topology

- Local URL (reserved): `https://compliance.dev.localhost:44399`. The Shell menu seed registers a **Compliance** microservice there, owning the "Compliance Monitor" item (`/v1/compliance/view-all`).
- The IDP demo data assigns `olivia.compliance` to Compliance case `COMP-10045`.
