# Enterprise Web Platform V3 — Banking Services Reference Architecture

Enterprise Web Platform V3 (EWP V3) is a demonstration banking-services platform. It is built as an **architectural reference**: a working example of how enterprise concerns compose without sliding into a distributed monolith.

- **Bounded contexts.** Customer Onboarding, Customer KYC, Compliance, Accounts, Payments, Notifications and Documents Management are implemented. Each is governed by **Domain-Driven Design**, owns its own database, and is independently deployable.
- **Micro-frontends.** Each business context has its own MFE, hosted in iframes by a business-neutral **Shell** that provides branding, navigation and the Application Workspace.
- **Security boundaries.** Each MFE sits behind its own **BFF**. A BFF calls its own domain API with the **user's access token**, and other contexts' APIs with **M2M tokens**, keeping human identity and service identity separate.
- **Identity.** **Duende IdentityServer 8** provides OpenID Connect and OAuth 2.1 (Authorization Code + PKCE, Client Credentials).
- **Authorization** goes beyond RBAC: **ABAC, ReBAC, workflow-state authorization and Separation of Duties**.
- **Asynchronous workflows** use **CQRS, Transactional Outbox, Kafka**, idempotent consumers and **sagas** (choreography for onboarding, orchestration for payments).
- **Traceability.** Workflow, correlation and causation IDs and the accountable human initiator are carried end to end. Human approvals (e.g. KYC review) are accountable steps.
- **Operations.** Resilience, rate limiting, observability, auditability and **SignalR** user notifications are covered at different stages of maturity (see the capability matrix).
- **New contexts** follow the customer's pattern: Next.js SPA with a light BFF → NestJS Journey API → ASP.NET Core Domain APIs. Existing contexts keep their current stack.

> EWP V3 is an architectural PoC, not a production banking system and not a claim of regulatory compliance.

---

## Deployable components

| Component | Path | Technology | Status |
|---|---|---|---|
| Identity Provider | `src/IDP` | Duende IdentityServer 8, ASP.NET Core 10 | Present |
| Shell (BFF + SPA) | `src/Shell` | ASP.NET Core 10 + Next.js | Present |
| Customer Onboarding (MFE/BFF, API, Outbox relay, Onboarding Outcome Subscriber) | `src/Microservices/CustomerOnboarding`, `src/AsyncWorkflows/Publishers/CustomerOnboarding`, `src/AsyncWorkflows/Subscribers/CustomerOnboarding` | Next.js, ASP.NET Core 10, .NET workers | Present |
| Customer KYC (MFE/BFF, API, KYC Case Opening Subscriber) | `src/Microservices/CustomerKyc`, `src/AsyncWorkflows/Subscribers/CustomerKyc` | Next.js, NestJS, ASP.NET Core 10, .NET worker | Present |
| Documents Management (API, Document Invalidation Subscriber) | `src/Microservices/DocumentsManagement`, `src/AsyncWorkflows/Subscribers/DocumentsManagement` | ASP.NET Core 10, .NET worker | Present |
| Compliance (MFE/BFF, API, Compliance Case Opening Subscriber) | `src/Microservices/Compliance`, `src/AsyncWorkflows/Subscribers/Compliance` | Next.js, ASP.NET Core 10, .NET worker | Present |
| Screening Provider Simulator (stand-in for an external AML vendor) | `src/Simulators/ScreeningProviderSimulator` | ASP.NET Core 10 | Present |
| Accounts (MFE/BFF, API, Account Application Opening Subscriber) | `src/Microservices/Accounts`, `src/AsyncWorkflows/Subscribers/Accounts` | Next.js, ASP.NET Core 10, .NET worker | Present |
| Core Banking Simulator (stand-in for the core-banking system) | `src/Simulators/CoreBankingSimulator` | ASP.NET Core 10 | Present |
| Notifications (API with SignalR hub, Notifications Subscriber; displayed by the Shell) | `src/Microservices/Notifications`, `src/AsyncWorkflows/Subscribers/Notifications` | ASP.NET Core 10, SignalR, .NET worker | Present |
| Payments (MFE/BFF, API with the saga orchestrator, Payments Saga Reply Subscriber; Accounts Command Subscriber on the Accounts side) | `src/Microservices/Payments`, `src/AsyncWorkflows/Subscribers/Payments`, `src/AsyncWorkflows/Subscribers/Accounts/AccountsCommandSubscriber` | Next.js, ASP.NET Core 10, .NET workers | Present |
| Audit (Next.js SPA + light BFF; NestJS Journey API; Domain API with the trail's Kafka consumer inside - the customer's pattern, token exchange at every hop) | `src/Microservices/Audit` | Next.js, NestJS, ASP.NET Core 10 | Present |
| Payment Network Simulator (stand-in for the payment network: BSB directory, Confirmation of Payee) | `src/Simulators/PaymentNetworkSimulator` | ASP.NET Core 10 | Present |
| BFF state (sessions and Data Protection keys of the .NET BFFs) | `db/EwpBffStateDb.sql` | PostgreSQL, one schema per BFF | Present |
| Infrastructure | PostgreSQL 18 and Kafka 4 (KRaft) installed natively; Kafka secured with [ps/ps/kafka/Setup-KafkaSecurity.ps1](kafka/README.md) | SCRAM-SHA-512 users, per-topic ACLs, Kafka UI (read-only) | Present |
| Observability | `src/Common/Observability` | OpenTelemetry traces across HTTP and Kafka; Serilog structured logs with the trace ID; Prometheus `/metrics`; all three over OTLP when configured; `/health/live` and `/health/ready` on every component | Present |

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
| [Payment Saga diagram](doc/Payment%20Saga%20Orchestration.png) ([PDF](doc/Payment%20Saga%20Orchestration.pdf)) | The Payments saga's steps, replies, timeouts and compensation in one picture |
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

- BFFs: [CO](src/Microservices/CustomerOnboarding/BFF.Web/README.md), [KYC](src/Microservices/CustomerKyc/BFF.Web/README.md), [Compliance](src/Microservices/Compliance/BFF.Web/README.md), [Accounts](src/Microservices/Accounts/BFF.Web/README.md), [Payments](src/Microservices/Payments/BFF.Web/README.md)
- APIs: [DM API](src/Microservices/DocumentsManagement/API/README.md), [Notifications API](src/Microservices/Notifications/API/README.md), [Payments API](src/Microservices/Payments/API/README.md), [Audit API](src/Microservices/Audit/API/README.md), [Audit Journey API](src/Microservices/Audit/JourneyApi/README.md), [Audit web](src/Microservices/Audit/Web/README.md)
- Subscribers: [KYC Case Opening](src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/README.md), [Compliance Case Opening](src/AsyncWorkflows/Subscribers/Compliance/ComplianceCaseOpeningSubscriber/README.md), [Account Application Opening](src/AsyncWorkflows/Subscribers/Accounts/AccountApplicationOpeningSubscriber/README.md), [Onboarding Outcome](src/AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/README.md), [Document Invalidation](src/AsyncWorkflows/Subscribers/DocumentsManagement/DocumentInvalidationSubscriber/README.md), [Accounts Command](src/AsyncWorkflows/Subscribers/Accounts/AccountsCommandSubscriber/README.md), [Payments Saga Reply](src/AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/README.md), [Notifications](src/AsyncWorkflows/Subscribers/Notifications/NotificationsSubscriber/README.md)
- Simulators: [Screening Provider](src/Simulators/ScreeningProviderSimulator/README.md), [Core Banking](src/Simulators/CoreBankingSimulator/README.md), [Payment Network](src/Simulators/PaymentNetworkSimulator/README.md)
- [Design system](src/Common/DesignSystem/README.md)

### Local development

[ReadMe.txt](ReadMe.txt): host names, HTTPS, databases, Kafka topics, start-up, troubleshooting and Bruno API testing.

---

## Prerequisites

- .NET 10 SDK
- Node.js 18+ and pnpm
- PostgreSQL 18
- Apache Kafka 4.x (KRaft) at `C:\Kafka` and a Java runtime 21+ (see [kafka/README.md](kafka/README.md))
- Optional: Jaeger for viewing traces; Kafka UI for browsing topics
- Visual Studio 2026 and / or VS Code

`docker-compose.yml` is an older alternative for PostgreSQL and Kafka. Its Kafka runs **without authentication**, so it does not match the components' SASL settings; to use it, set `Kafka:SecurityProtocol` to `Plaintext` in each component.

Start with [ReadMe.txt](ReadMe.txt).