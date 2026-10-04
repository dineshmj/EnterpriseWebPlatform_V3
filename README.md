# Enterprise Web Platform V3 — Banking Services Reference Architecture

Enterprise Web Platform V3 (EWP V3) is a demonstration banking-services platform. It is built as an **architectural reference**: a working example of how enterprise concerns compose without sliding into a distributed monolith.

- **Bounded contexts.** Customer Onboarding, Customer KYC, Compliance (backend) and Documents Management are implemented; Accounts and Payments are planned. Each is governed by **Domain-Driven Design**, owns its own database, and is independently deployable.
- **Micro-frontends.** Each business context has its own MFE, hosted in iframes by a business-neutral **Shell** that provides branding, navigation and the Application Workspace.
- **Security boundaries.** Each MFE sits behind its own **BFF**. A BFF calls its own domain API with the **user's access token**, and other contexts' APIs with **M2M tokens**, keeping human identity and service identity separate.
- **Identity.** **Duende IdentityServer 8** provides OpenID Connect and OAuth 2.1 (Authorization Code + PKCE, Client Credentials).
- **Authorization** goes beyond RBAC: **ABAC, ReBAC, workflow-state authorization and Separation of Duties**.
- **Asynchronous workflows** use **CQRS, Transactional Outbox, Kafka**, idempotent consumers and **sagas** (choreography for onboarding, orchestration for payments).
- **Traceability.** Workflow, correlation and causation IDs and the accountable human initiator are carried end to end. Human approvals (e.g. KYC review) are accountable steps.
- **Operations.** Resilience, observability, auditability and **SignalR** user notifications are covered at different stages of maturity (see the capability matrix).

> EWP V3 is an architectural PoC, not a production banking system and not a claim of regulatory compliance.

---

## Deployable components

| Component | Path | Technology | Status |
|---|---|---|---|
| Identity Provider | `src/IDP` | Duende IdentityServer 8, ASP.NET Core 10 | Present |
| Shell (BFF + SPA) | `src/Shell` | ASP.NET Core 10 + Next.js | Present |
| Customer Onboarding (MFE/BFF, API, Outbox relay, Onboarding Outcome Subscriber) | `src/Microservices/CustomerOnboarding`, `src/AsyncWorkflows/Publishers/CustomerOnboarding`, `src/AsyncWorkflows/Subscribers/CustomerOnboarding` | Next.js, ASP.NET Core 10, .NET workers | Present |
| Customer KYC (MFE/BFF, API, KYC Case Opening Subscriber) | `src/Microservices/CustomerKyc`, `src/AsyncWorkflows/Subscribers/CustomerKyc` | Next.js, NestJS, ASP.NET Core 10, .NET worker | Present |
| Documents Management (API) | `src/Microservices/DocumentsManagement` | ASP.NET Core 10 | Present |
| Compliance (API, Compliance Case Opening Subscriber; MFE/BFF planned) | `src/Microservices/Compliance`, `src/AsyncWorkflows/Subscribers/Compliance` | ASP.NET Core 10, .NET worker | Present (backend) |
| Screening Provider Simulator (stand-in for an external AML vendor) | `src/Simulators/ScreeningProviderSimulator` | ASP.NET Core 10 | Present |
| Accounts, Payments | `src/Microservices/…` | — | Planned |
| Infrastructure | PostgreSQL 18 and Kafka 4 (KRaft) installed natively; Kafka secured with [kafka/Setup-KafkaSecurity.ps1](kafka/README.md) | SCRAM-SHA-512 users, per-topic ACLs, Kafka UI (read-only) | Present |
| Observability | `src/Common/Observability` | OpenTelemetry traces across HTTP and Kafka (OTLP, e.g. Jaeger); `/health/live` and `/health/ready` on every component | Present |

---

## Documentation map

Each document has one purpose. A fact is written in exactly one place; other documents link to it.

### Platform-wide (`doc/`)

| Document | Owns |
|---|---|
| [Architectural Vision & Security Blueprint](doc/Enterprise-Web-Platform-V3-Architectural-Vision-and-Security-Blueprint.md) | Vision, principles, landscape, context map, DDD and deployability rules, cross-cutting security and operations targets, **capability matrix**, roadmap, status legend |
| [Architectural & Security Features (demoable)](doc/Architectural-And-Security-Features-Demoable-EWP-V3.md) | What can be seen working today: patterns, security controls and avoided anti-patterns, each with where to look in the code; starts with a "common questions" index (DLQ handling, pod replacement, IDOR, tokens…) |
| [Application Personas](doc/Application-Personas.md) | The persona catalogue, role codes, cross-persona principles |
| [Authorization Model](doc/Authorization-Model.md) | How authorization decisions are made: pipeline, scopes vs permissions, ABAC / ReBAC / SoD mechanics, platform-level permissions |
| [Saga Plans](doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md) | Cross-context workflows: event chain, compensation, saga rules |
| [Integration Event Catalogue](doc/Integration-Event-Catalogue.md) | Event envelope, topics, keys, producers, consumers |
| [End-to-End Processing Walkthrough](doc/End-to-End-Processing-Walkthrough.md) | One onboarding traced from the agent's submit to the KYC outcome, through every component, policy and domain check |
| [Architect Review](doc/Fellow-architect-review-of-v3-ewp.md) | Point-in-time review findings and their remediation status |

### Per component (`src/**/doc/`)

Business requirements of one component: boundary, persona rules within it, permissions, domain model, states, business rules, integration, implementation status and gaps.

| Component | Requirements |
|---|---|
| Customer Onboarding | [CustomerOnboarding-Requirements.md](src/Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md) |
| Customer KYC | [CustomerKyc-Requirements.md](src/Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md) |
| Documents Management | [DocumentsManagement-Requirements.md](src/Microservices/DocumentsManagement/doc/DocumentsManagement-Requirements.md) |
| Compliance | [Compliance-Requirements.md](src/Microservices/Compliance/doc/Compliance-Requirements.md) |
| Accounts | [Accounts-Requirements.md](src/Microservices/Accounts/doc/Accounts-Requirements.md) |
| Payments | [Payments-Requirements.md](src/Microservices/Payments/doc/Payments-Requirements.md) |
| Local Kafka (security set-up, users and ACLs, Kafka UI) | [kafka/README.md](kafka/README.md) |
| Identity Provider (clients, scopes, claims, **demo users**) | [IDP-Requirements.md](src/IDP/doc/IDP-Requirements.md) |
| Shell (menu, workspace, MFE protocol, logout, notifications) | [Shell-Requirements.md](src/Shell/doc/Shell-Requirements.md) |

### Technical how-to (next to the code)

How to build, configure and run one deployable:

- [CO BFF README](src/Microservices/CustomerOnboarding/BFF.Web/README.md)
- [KYC BFF README](src/Microservices/CustomerKyc/BFF.Web/README.md)
- [KYC Case Opening Subscriber README](src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md)
- [Onboarding Outcome Subscriber README](src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md)
- [DM API README](src/Microservices/DocumentsManagement/API/README.md)

### Local development

[ReadMe.txt](ReadMe.txt): host names, HTTPS, databases, Kafka topics, start-up, troubleshooting and Bruno API testing.

---

## Prerequisites

- .NET 10 SDK
- Node.js 18+ and pnpm
- PostgreSQL 18
- Apache Kafka 4.x (KRaft) at `C:\Kafka` and a Java runtime 21+ (see [kafka/README.md](kafka/README.md))
- Optional: Jaeger for viewing traces; Kafka UI for browsing topics

`docker-compose.yml` is an older alternative for PostgreSQL and Kafka. Its Kafka runs **without authentication**, so it does not match the components' SASL settings; to use it, set `Kafka:SecurityProtocol` to `Plaintext` in each component.
- Visual Studio 2026 and / or VS Code

Start with [ReadMe.txt](ReadMe.txt).
