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
| 07 | [Light BFFs (Next.js)](07-Light-BFFs-NextJs.md) | Audit web, Operations web, Partner Products web |
| 08 | [Journey APIs (NestJS)](08-Journey-APIs-NestJS.md) | Audit Journey API, Operations Journey API, Partner Products Journey API |
| 09 | [Domain APIs](09-Domain-APIs.md) | Customer Onboarding, KYC, Documents Management, Compliance, Accounts, Payments, Notifications, Audit, Partner Products (also EWP's adapter to the Partner Bank) |
| 10 | [Outbox publishers](10-Outbox-Publishers.md) | Customer Onboarding Outbox relay; in-process relays of KYC, Compliance, Accounts, Payments |
| 11 | [Kafka subscribers](11-Kafka-Subscribers.md) | The 8 subscriber workers; the Audit API's in-process trail subscriber |
| 12 | [Kafka broker and ACLs](12-Kafka-Broker-and-ACLs.md) | The local Kafka broker (KRaft) |
| 13 | [PostgreSQL database users](13-PostgreSQL-Database-Users.md) | One login per deployable, one database each |
| 14 | [PostgreSQL table privileges](14-PostgreSQL-Table-Privileges.md) | What each login may do inside its database |
| 15 | [Workflow workers (Temporal)](15-Workflow-Workers-Temporal.md) | The Partner Products Worker (Digital Gold purchases) |

## Every tier: third-party packages

These items apply to every deployable built from npm or NuGet packages (tiers 01–11 and 15).

- [x] **Each Node.js project audits clean** (`pnpm audit`: no known vulnerabilities in any of the 12 projects on 2026-10-15; the Partner Products Journey API and the new Worker on 2026-10-19 - the export now audits 15 projects). *Because* SCA tools (Snyk, Dependabot) flag vulnerable versions whether they run in production or only at build time. → each project's `pnpm-lock.yaml`
- [x] **A vulnerable transitive package is fixed by an override that rewrites only its vulnerable versions** (e.g. `source-map-js@>=1.0.0 <1.2.2: ^1.2.2`). *Because* the direct dependency (`next`, the Nest CLI) is pinned, and pnpm 12 reads overrides only from `pnpm-workspace.yaml`, not from `package.json`. → each project's `pnpm-workspace.yaml`
- [x] **Every export audits all Node.js projects** and warns (yellow, without failing) about a high or critical advisory. *Because* `pnpm install` only restores the locked versions: without the check a new advisory goes unseen until an external scan. → `ps/build/CompileAndExportBFFClients_V3.ps1`, step 8
- [x] **Unused tooling is removed rather than patched:** the Shell's ESLint (`next lint` no longer exists in Next 16, so it never ran) took `braces`, which has no fixed version, out of the tree. → `src/Shell/client-app/package.json`
- [x] **No vulnerable NuGet package** in the main solution or the test solution (`dotnet list package --vulnerable --include-transitive`, 2026-10-15). → the `.csproj` files
- [ ] **The NuGet check runs automatically** (today it is run by hand). Hardening idea: a CI step, or a step in `ps/Reset.ps1`.
- [ ] **Major versions are current.** Open, each a deliberate upgrade with a retest: NestJS 11 → 12 (KYC BFF, both Journey APIs); `jose` 4 → 6 and `openid-client` 5 → 6 (KYC BFF, Audit and Operations web, both Journey APIs - sign-in and token checks); TypeScript 5.9 → 7 in the Node.js services (the MFEs are on 7). Within the major: Next 16.3 → 16.4, React 19.2 → 19.3.
- [ ] **The machine's .NET runtime is the latest patch** of .NET 10 (installed 10.0.11; packages already 10.0.13). *Because* the shared framework carries its own security fixes.