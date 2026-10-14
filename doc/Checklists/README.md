# EWP V3 — Checklists per deployable tier

One checklist per kind of deployable. Each item has four parts:

- a checkbox;
- the statement;
- *because* — the reason;
- → where to see it.

`[x]` means the item is true of the code today; it was checked in the code, not taken from documentation. `[ ]` means hardening or production work that is still open. Every `[ ]` item is either recorded as a residual risk in [Threat-Model.md](../Threat-Model.md) §7, or named here as a hardening idea.

A new deployable of a tier should pass that tier's checklist before it is merged. A change to a tier should keep its checklist true.

| # | Tier | Deployables |
| --- | --- | --- |
| 01 | [Identity provider](01-IDP.md) | IDP (Duende IdentityServer) |
| 02 | [Shell SPA](02-Shell-SPA.md) | The Shell's Next.js static export |
| 03 | [Shell BFF](03-Shell-BFF.md) | The Shell's ASP.NET Core host (Duende BFF) |
| 04 | [Microservice MFE SPAs](04-MFE-SPAs.md) | Next.js static exports of Customer Onboarding, KYC, Compliance, Accounts, Payments |
| 05 | [Microservice MFE BFFs (.NET)](05-MFE-BFFs-DotNet.md) | Customer Onboarding, Compliance, Accounts, Payments BFFs |
| 06 | [KYC BFF (NestJS)](06-KYC-BFF-NestJS.md) | The KYC BFF |
| 07 | [Light BFFs (Next.js)](07-Light-BFFs-NextJs.md) | Audit web, Operations web |
| 08 | [Journey APIs (NestJS)](08-Journey-APIs-NestJS.md) | Audit Journey API, Operations Journey API |
| 09 | [Domain APIs](09-Domain-APIs.md) | Customer Onboarding, KYC, Documents Management, Compliance, Accounts, Payments, Notifications, Audit |
| 10 | [Outbox publishers](10-Outbox-Publishers.md) | Customer Onboarding Outbox relay; in-process relays of KYC, Compliance, Accounts, Payments |
| 11 | [Kafka subscribers](11-Kafka-Subscribers.md) | The 8 subscriber workers; the Audit API's in-process trail subscriber |
| 12 | [Kafka broker and ACLs](12-Kafka-Broker-and-ACLs.md) | The local Kafka broker (KRaft) |
| 13 | [PostgreSQL database users](13-PostgreSQL-Database-Users.md) | One login per deployable, one database each |
| 14 | [PostgreSQL table privileges](14-PostgreSQL-Table-Privileges.md) | What each login may do inside its database |