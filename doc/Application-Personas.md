# Enterprise Web Platform V3
## Application Personas

**Document:** Application Personas  
**Version:** 2.0  
**Domain:** Banking Services  
**Status:** Living document

---

## 1. Purpose

This document is the single catalogue of the **business personas** of EWP V3: who they are, what they are broadly responsible for, and which bounded contexts they work in.

It deliberately does **not** contain:

| Topic | Owned by |
|---|---|
| What a persona may / must not do inside a bounded context | That context's requirements document (see section 4) |
| Role codes, permission naming, the authorization decision pipeline, ABAC / ReBAC / SoD mechanics | [Authorization-Model.md](Authorization-Model.md) |
| Demo users, their attributes and relationships | [IDP requirements](../src/IDP/doc/IDP-Requirements.md) |
| Cross-context workflows | [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](EWP-V3-Saga-Choreography-and-Orchestration-Plans.md) |

---

## 2. Personas

| Persona | Role code | Primary responsibility | Works in |
|---|---|---|---|
| Customer | `customer` | Initiates and reviews their own banking activities | Customer Onboarding, Accounts, Payments |
| Customer Service Agent | `customer_service_agent` | Creates and manages customers and onboarding applications on a customer's behalf | Customer Onboarding |
| KYC Officer | `kyc_officer` | Verifies customer identity and submitted documents | Customer KYC |
| Compliance Officer | `compliance_officer` | Makes AML, risk and compliance decisions | Compliance |
| Account Officer | `account_officer` | Reviews and approves account opening | Accounts |
| Payments Officer | `payments_officer` | Reviews and processes payment instructions | Payments |
| Operations Administrator | `operations_administrator` | Monitors workflows and handles technical exceptions (retry, reprocess) | All contexts — operational views only |
| Auditor | `auditor` | Independent, read-only review of business activity and history | All contexts — read-only |
| Platform Administrator | `platform_administrator` | Administers the platform: users, roles, configuration, health | IDP, Shell — never business approvals |

A persona is a **business responsibility**, not a job title or a technical role. One employee may hold more than one role where organizational policy permits, but holding several roles never bypasses Separation of Duties.

---

## 3. Cross-Persona Principles

These principles apply to every persona and every bounded context:

1. **Operational or technical authority is not business authority.** An Operations Administrator may retry a failed workflow but cannot thereby approve a KYC case or a payment. A Platform Administrator may manage roles but cannot approve anything.
2. **Nobody approves their own work** where Separation of Duties requires independence. In particular, a customer never approves their own onboarding.
3. **The Auditor never mutates** business data, workflow state or audit records.
4. **Accountability follows the human.** When a person starts a long-running workflow, they remain its accountable initiator even while machine identities execute later steps.
5. **Notifications go only to the people who should see them** — never to every signed-in user.

The approval chain for Customer Onboarding illustrates how the personas hand over to one another:

```text
Customer Service Agent ──submits──► KYC Officer ──verifies──► Compliance Officer ──approves──► Account Officer ──opens account
```

How these principles are enforced is described in [Authorization-Model.md](Authorization-Model.md).

---

## 4. Where Each Persona's Detailed Rules Live

| Bounded context | Requirements document |
|---|---|
| Customer Onboarding | [CustomerOnboarding-Requirements.md](../src/Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md) |
| Customer KYC | [CustomerKyc-Requirements.md](../src/Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md) |
| Compliance | [Compliance-Requirements.md](../src/Microservices/Compliance/doc/Compliance-Requirements.md) |
| Accounts | [Accounts-Requirements.md](../src/Microservices/Accounts/doc/Accounts-Requirements.md) |
| Payments | [Payments-Requirements.md](../src/Microservices/Payments/doc/Payments-Requirements.md) |
| Documents Management | [DocumentsManagement-Requirements.md](../src/Microservices/DocumentsManagement/doc/DocumentsManagement-Requirements.md) |
| Shell | [Shell-Requirements.md](../src/Shell/doc/Shell-Requirements.md) |
| Identity Provider | [IDP-Requirements.md](../src/IDP/doc/IDP-Requirements.md) |

---

## 5. Guiding Principle

> **A user is authorized not merely by who they are, but by what they are responsible for, what they are permitted to do, their relationship to the business resource, and the current state of the business process.**
