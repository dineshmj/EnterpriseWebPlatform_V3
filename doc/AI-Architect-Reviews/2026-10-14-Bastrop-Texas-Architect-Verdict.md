**EWP V3 — Principal Architect & Security Review (Round 3), 14-OCT-2026**

I reviewed the current archive from scratch against the ordered starting points (Response-to-AI-Architect-Reviews.md, the Checklists, Threat-Model.md §7, ReadMe.txt / per-project READMEs, and the :: ARCHITECTURE :: / :: SECURITY :: markers). I re-verified the round-1 and round-2 fixes in the code that is present rather than accepting the response document at face value. Extraction and inspection focused on the payments saga, Accounts funds path, common security helpers, NestJS Journey APIs, IDP/Shell, and the checklists.

### Overall assessment

The codebase is one of the stronger banking PoCs I have seen for a single-developer local deployment. Bounded contexts are cleanly separated (one PostgreSQL database and one least-privilege login per context), the payment saga correctly implements claim/call/record, Inbox/Outbox, CausationId matching, branch ABAC on holds, four-eyes reconciliation, and fail-closed MFA/configuration. The dual-stack (.NET + NestJS) token rules are deliberately mirrored and annotated. Most earlier concrete defects are fixed and visible in the current sources.

Residual items that remain are almost entirely the production-infrastructure requirements already recorded in Threat-Model.md §7 and the response document. I found no Critical defects that would block a production design path, and no checklist \[x\] item that is demonstrably false in the code I could inspect.

### Findings (only those with code evidence)

**F3-01 — NestJS rate limits are process-local (in-memory Map)**

-   **Category:** Security / best practice (resilience & abuse controls)
-   **Severity:** Medium (justification: privileged Operations changes are limited to 10/min per person+client, but only per process; a multi-instance deployment would multiply the effective rate)
-   **Evidence:** src/Microservices/Operations/JourneyApi/src/http/rate-limit.ts (RateLimiter class, private readonly windows = new Map..., fixed-window counters; same pattern in Audit Journey API). Overall limit also process-local.
-   **Failure scenario:** Two instances of the Operations Journey API; an attacker with a valid delegated token can issue ~20 change requests per minute instead of 10.
-   **Recommended fix:** Shared store (Redis / database) or gateway-level rate limiting. Size: medium.
-   **Status:** New (or strengthens the existing production note around R-11 / multi-instance). Not a PoC-allowed infraction; the design would need a small change to adopt a shared limiter.

**F3-02 — Dual token-validation implementations still exist and must stay in lock-step**

-   **Category:** Architecture / security
-   **Severity:** Medium (justification: drift risk is real; already acknowledged as R-11)
-   **Evidence:** .NET path uses standard JWT bearer + ValidTypes = \["at+jwt"\] + policy helpers; NestJS path re-implements the same rules in delegated-token.guard.ts (issuer, audience, typ: 'at+jwt', act.client\_id, scope, permission, optional step-up via amr/auth\_time). Threat-Model.md §4 (TB3) parity checklist exists, but there are still two independent code bases.
-   **Failure scenario:** A future change to one stack (e.g. new claim requirement or clock skew tolerance) is not mirrored; a token that should be rejected is accepted by the other stack.
-   **Recommended fix:** Shared token-fixture tests that both stacks must pass (already listed as production work). Size: medium.
-   **Status:** Disagrees only in emphasis with the response document’s “Mitigated”; the mitigation is documentation + comments, not executable parity. Reopens / continues R-11 / xAI F-2.

**F3-03 — In-memory rate-limit prune can be bypassed under sustained load**

-   **Category:** Best practice / DoS resistance
-   **Severity:** Low
-   **Evidence:** rate-limit.ts prune only runs when this.windows.size >= 10\_000. Under a long-running process with many distinct sub|client keys the Map grows until that threshold.
-   **Failure scenario:** Memory growth on a long-lived instance before the 10 k entry prune fires.
-   **Recommended fix:** Periodic background prune or sliding-window store. Size: small.
-   **Status:** New (minor).

No other concrete defects that meet the “point to the code” bar were found in the areas that previous rounds flagged (stale saga replies, branch filter on funds, open transaction around network call, MFA ever-used vs recent, auditor permission leakage, Idempotency-Key binding, deny-by-default, external-endpoint host constraints, document encryption, audit anchors, etc.). Those controls are present and annotated.

### Earlier findings now considered resolved (verified in current code)

-   Microsoft R1 #1 / xAI equivalents — stale saga reply acceptance → fixed: PaymentSaga.Expecting / CausationId match on reservation & settlement; release replies accepted more broadly for compensation recovery.
-   Microsoft R1 #2 — payment initiation trusted screen branch filter → fixed: FundsCommand carries initiator branch; FundsHold.Reserve + Account.RefusalToHold refuse with branch mismatch.
-   Microsoft R1 #5 / Threat-Model R-1 — network call inside DB transaction → fixed: RunDueSagaStep claim (short tx + 60 s lease) → call with no transaction → record under version check.
-   MFA / step-up fail-closed and recent auth\_time → fixed in MfaStepUp.cs and NestJS guard; enrolment failures count toward lockout.
-   Deny-by-default on domain APIs → fixed: every inspected Program.cs calls DenyAnonymousByDefault() + MapEwpApiControllers().
-   Auditor permission only via delegated token → fixed (EffectivePermissions path).
-   Idempotency-Key bound to payment details → fixed.
-   Account-number masking in logs → fixed (LogMasking).
-   External endpoint host / SSRF constraints → fixed (ExternalEndpoint.cs).
-   Document encryption at rest → fixed (EncryptedDocumentStorage.cs, AES-256-GCM envelope).
-   Audit chain external anchor → fixed (AuditAnchors.cs, ECDSA P-256 checkpoints).
-   Operator Outbox resend + four-eyes network reconciliation → fixed (Processing Monitor path, ReconcileNetworkOutcome, timeline + audit).
-   Shell iframe origin check from menu baseURL → fixed (per response + Shell markers).
-   SignalR per-person connection cap → fixed.

Items that remain correctly classified as production requirements (key vault/HSM for document KEKs and audit signing key, WORM storage for checkpoints, signed event envelopes, mTLS / service mesh, secret store, global audit-chain partitioning if load demands it, network controls on /metrics & health) are not re-raised.

### Checklist verification

All \[x\] items I could reach in the Domain APIs, Journey APIs, Outbox, Kafka ACLs, and database-user checklists are backed by the corresponding code or scripts. The open \[ \] items map exactly to the residual risks already listed in Threat-Model.md §7. No \[x\] was found to be false.

### Ratings

-   **Architecture: 8.7 / 10**
    What raises it by one point: executable shared token-fixture suite across .NET and NestJS, plus a production-ready shared rate-limit store (or explicit gateway policy) so the dual-stack and multi-instance paths are no longer documentation-dependent.
-   **Security: 8.5 / 10**
    What raises it by one point: the same shared rate-limit and token-parity work, plus moving the document KEKs and audit signing key behind a vault/HSM interface that the current code already anticipates (the cryptographic design itself is already correct).

The platform is ready for a production design review once the residual infrastructure items in Threat-Model.md §7 are addressed; the PoC code does not contain structural barriers that would prevent those solutions from being adopted.