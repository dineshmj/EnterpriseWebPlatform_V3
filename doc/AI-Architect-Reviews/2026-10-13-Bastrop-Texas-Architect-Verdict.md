# EWP V3 — Re-review (Principal Architect / Security)

Today's date: 13-OCT-2026.

**Scope:** Static review of the current tree (EnterpriseWebPlatform\_V3\_20261013\_160122). Nothing was executed.
**Method:** Started from ReadMe.txt, doc/Threat-Model.md, and component READMEs (Security controls tables), then verified claims in code. PoC-allowed infractions from your list and Threat-Model §7 “Accepted for PoC” are excluded unless they hide a design flaw.

---

## Earlier findings — verified resolved

| Prior issue | Status | Evidence |
| --- | --- | --- |
| Outbox relay stall / unsafe publishing | **Resolved** | FOR UPDATE SKIP LOCKED, bounded attempts, park + ops resend (ResendParkedMessage, OutboxController, Payments README #17) |
| Documents auth scope-only / branch via header | **Resolved** | DocumentResourceAuthorization — branch from token, acting client allow-list, delete only CO BFF |
| Stored-XSS via documents | **Resolved** | Magic-byte policy + nosniff + attachment disposition |
| CO RBAC-only / caller-supplied SubjectId/BranchId | **Resolved** | CustomerResourceAuthorization — branch/city/country from claims |
| Secrets in shared libraries | **Resolved** | Config-driven, fail-closed (ClientSecretStore, subscriber Validate) |
| Refresh token reuse | **Resolved** | RefreshTokenUsage = OneTimeOnly on clients |
| Missing ValidTypes = at+jwt | **Resolved** | All domain APIs |
| Server-side sessions | **Resolved** | IDP AddServerSideSessions |
| Document encryption at rest | **Resolved** | EncryptedDocumentStorage AES-256-GCM envelope, fail-closed key ring |
| Audit chain not externally anchored | **Resolved** | ECDSA P-256 checkpoints, write-once files (AuditAnchors.cs) |
| MFA step-up only “ever authenticated with MFA” | **Resolved** | amr=mfa **and** auth\_time ≤ Mfa:StepUpMaxAgeMinutes (MfaStepUp.cs) |
| No ops recovery for parked outbox / blind release | **Resolved** | Resend with reason + MFA; saga CONFIRM\_WITH\_NETWORK by Idempotency-Key |
| SignalR connection quota | **Resolved** | Notifications:MaxConnectionsPerPerson |
| No circuit breakers on outbound HTTP | **Resolved** | AddStandardResilienceHandler on subscribers and payment network client |
| Shared BFF key material | **Resolved** | Per-BFF schema + role in EwpBffStateDb; SetApplicationName + cert-protected DP keys |

---

## Remaining findings

### F-1 — Customer KYC API has no global deny-by-default on endpoints

**Category:** Security (ASVS / OWASP API5)
**Severity:** Medium — a new controller without \[Authorize\] would be reachable without an authorization policy (and, depending on middleware order, possibly anonymously).

**Evidence:**

-   src/Microservices/CustomerKyc/API/Program.cs — app.MapControllers(); with **no** .RequireAuthorization().
-   Contrast: Accounts, Payments, Compliance, Notifications, Audit, Shell use MapControllers().RequireAuthorization().
-   Current KYC controllers *do* carry \[Authorize(...)\], so behaviour today is correct.

**Failure scenario:** Developer adds KycReportsController without attributes → endpoint is not protected by a policy.

**Fix (small):**

C#

```
app.MapControllers().RequireAuthorization();
```

Keep per-action policies for finer scopes. Optionally set FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build().

**Prior?** Implicit in earlier “deny by default” commentary; **unchanged gap** on this service.

---

### F-2 — Dual auth stacks (.NET APIs vs NestJS Journey APIs) can drift

**Category:** Architecture / best practice
**Severity:** Medium — same security rules implemented twice (JWT typ, audience, act, permissions, MFA age) in C# and TypeScript.

**Evidence:**

-   .NET: JWT Bearer + policy handlers (e.g. Payments, Audit API).
-   NestJS: delegated-token.guard.ts in Audit and Operations Journey APIs (manual jose verify, scope, act.client\_id, recentMfa).

**Failure scenario:** MFA max-age or act parsing is tightened in one stack and forgotten in the other → inconsistent enforcement or a bypass on one path.

**Fix (medium):** Shared contract tests (token fixtures) that both stacks must pass; or generate policy metadata from one source. Document the parity checklist in the threat model.

**Prior?** **New** (Journey APIs appeared after the last review).

---

### F-3 — MFA step-up forces full sign-out instead of OIDC step-up

**Category:** Best practice / security UX
**Severity:** Low–Medium — control is correct (amr + fresh auth\_time), but the recovery path is “sign out and sign in again,” which increases friction and support risk.

**Evidence:**

-   MfaStepUp.cs / NestJS recentMfa — refuse when auth\_time is stale.
-   Threat-Model R-4 notes polish: OIDC max\_age / acr\_values.
-   Problem detail text tells the user to sign out.

**Failure scenario:** Officer’s session is hours old; high-value approve fails with 403; user abandons the flow or seeks workarounds.

**Fix (medium):** BFF-initiated re-auth with max\_age / acr\_values=mfa so the existing session can be stepped up without full logout.

**Prior?** Called out as residual polish in Threat-Model; **unchanged**.

---

### F-4 — Production key custody for document KEKs and audit anchors is still design work

**Category:** Security (encryption at rest / audit integrity)
**Severity:** Medium *for production go-ahead* (implementation in PoC is solid; operational model is not finished).

**Evidence:**

-   DocumentKeyRing loads KEKs from configuration (fail-closed). Threat-Model R-2: production should use vault/HSM.
-   AuditAnchorStore writes signed checkpoints to a local directory marked read-only. R-3: production needs immutable store under a different administrator + key in HSM.

**Failure scenario:** In production with the same design, a single privileged admin with DB + config + filesystem can still rewrite history or decrypt evidence after key compromise.

**Fix (large for true production):**

-   Document KEKs only from KMS/HSM; rotation runbook.
-   Anchor checkpoints to WORM/object-lock storage; split admin roles (trail writer ≠ anchor writer).

**Prior?** Previously reported as missing encryption/anchors; **partly fixed** (crypto present; custody model open).

---

### F-5 — Inconsistent global authorization style across domain APIs

**Category:** Best practice / maintainability
**Severity:** Low (related to F-1).

**Evidence:** CO and DM use .RequireAuthorization("ApiScope"); others use bare .RequireAuthorization(); KYC uses neither at map level.

**Failure scenario:** Reviewers and new services copy the weakest pattern.

**Fix (small):** One shared extension, e.g. MapEwpApiControllers(), always applying authenticated fallback + optional scope policy.

**Prior?** **New** as an explicit consistency finding.

---

### F-6 — External payment-network BaseUrl is not host-constrained

**Category:** Security (SSRF defence-in-depth)
**Severity:** Low — URL is configuration, not caller-controlled; still worth locking down.

**Evidence:**

-   PaymentNetworkOptions.BaseUrl validated only as absolute URI (Program.cs Validate).
-   Client uses BaseAddress + relative paths; API key header.

**Failure scenario:** Misconfiguration (or compromised config store) points BaseUrl at an internal metadata service; the process still sends the API key.

**Fix (small):** Allow-list of hostnames / deny private/link-local ranges at startup when not Development.

**Prior?** **New**.

---

### F-7 — Audit trail actor identity is still envelope-trusted (not signed)

**Category:** Security / regulatory evidence
**Severity:** Low in PoC (Threat-Model R-6 accepted); **raise to Medium if production is claimed without change**.

**Evidence:** Actor fields come from producer envelopes; Kafka ACLs + trusted producers are the control. No message-level signature binding actor to payload.

**Failure scenario:** Compromised producer credentials publish events with a forged InitiatedByUserId / decision actor → audit attributes the wrong person.

**Fix (medium–large):** Sign integration events (or at least actor + message id) with a producer key; Audit verifies before append.

**Prior?** Accepted for PoC — listed only so production criteria stay explicit.

---

## What is *not* wrong (checked and sound)

-   Database-per-context; least-privilege DB roles and BFF schema isolation.
-   Token exchange (RFC 8693) with pinned acting clients; auditors only via Audit Journey API.
-   SoD in domain aggregates (KYC, Compliance, Accounts, Payments).
-   Branch ABAC on documents, customers, payments; no branch from headers.
-   Transactional outbox + inbox; saga lease/version; network reconciliation; parked-message resend with MFA + reason + audit trail.
-   CSP hash-based (scripts + styles); document content allow-list; log masking for account numbers.
-   Rate limiting (API sliding windows; IDP login/MFA fixed window).
-   SignalR: no client-driven group join; audiences from token; connection quota.
-   Fail-closed configuration for secrets, Kafka SASL, document keys, Data Protection certificates.

---

## Overall ratings

| Dimension | Score | What raises it by ~1 point |
| --- | --- | --- |
| **Architecture** | **8.5 / 10** | Unifyed parity tests for NestJS vs .NET auth (F-2); uniform endpoint authorization helper (F-1/F-5); finished production key/anchor custody design (F-4). |
| **Security** | **8.5 / 10** | Global deny-by-default on KYC (F-1); OIDC step-up instead of forced logout (F-3); host allow-list for external BaseUrls (F-6); event-level actor binding if claiming production audit strength (F-7). |

These scores assume PoC constraints. Against a **production banking** bar with no PoC exceptions, architecture would sit nearer **7.5** and security nearer **7** until F-4 and deployment controls (mTLS, secret store, WORM anchors) are real—not because the application design is weak, but because custody and network controls are still explicit residuals.

---

## Summary

The platform has closed almost every material issue from the previous review with real code, not documentation theatre. The threat model is living and mostly honest. What remains for a production go-ahead is:

1.  Close the KYC global authorization gap (small, do now).
2.  Prevent NestJS/.NET auth drift (tests or shared contract).
3.  Finish key/anchor custody for documents and audit (design + ops, not more AES).
4.  Prefer OIDC step-up over sign-out for MFA freshness.

Nothing found would justify blocking further PoC development. Nothing found is a free pass for a regulated production cutover without addressing F-1, F-2, and F-4.