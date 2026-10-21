**EWP V3 + Partner Bank — Round 4 Architectural & Security Review**
21-OCT-2026

I followed the ordered reading list in Response-to-AI-Architect-Reviews.md (“Round 4 — start here”), the Partner Bank doc/Architecture.md (D1–D15) and doc/Threat-Model.md (T1–T28, R1–R10), the Temporal checklist, the Partner Products requirements, and the design conversations. I then verified the three interaction patterns and the new code paths against the stated purpose of each, using only evidence present in the two repositories.

No finding below repeats an item already recorded as known, accepted for the PoC, planned, a production requirement, or a residual risk (including Partner Bank R1–R10 and EWP R-10 / R-14 / R-15 / R-16). Where a residual risk exists, the code matches the document’s description.

---

### Findings

**None that meet the bar of Defect / Production requirement with code evidence that contradicts the documents.**

The new Partner Products / Temporal / Partner Bank surface was examined specifically for the areas the response document asked reviewers to focus on (delegation pins, worker machine identity, signed-instruction replay, workflow endings, signal safety, step-up framing, A4 configuration pins, B8 Kafka TLS). All of those controls are present and behave as documented.

---

### Architecture score: **9.0 / 10**

Clean separation of the three Partner Bank interaction patterns, durable Temporal workflow with business-reference-only history, stable instruction IDs, and transactional Outbox still used for domain events. The half-point deduction is only for the remaining production Temporal hardening already listed as R-16.

### Security score: **8.8 / 10**

mTLS + certificate-bound token + ES256 signed instructions (separate keys), jti once-only on both sides, worker pinned to machine client with no sub/act, amount/payee/key checks before allocation, Kafka SASL\_SSL with host-name verification, and a narrow Shell-only framing exception for step-up. The residual score reflects the already-accepted PoC items (no revocation, keys in files, in-memory staff views, Temporal without mTLS).

---

### Three things checked and found sound

1.  **Pattern B — distributor signed instructions (mTLS + bound token + ES256 JWT)**
    EWP: PartnerBankClient.Sign produces typ: partner-instruction+jwt, audience partner-api, 5-minute life, jti = stable instruction ID; SocketsHttpHandler supplies the client certificate and validates the server against the PoC root only (PartnerBankCredentials.IsTrustedServer).
    Partner Bank: SignedInstructions rejects wrong issuer, wrong type, expired or unsigned tokens; GoldDesk.OpenAccountAsync / PlacePurchaseAsync key on InstructionId so a resend after a lost answer is never a second account or order.
    Evidence: src/Microservices/PartnerProducts/API/PartnerBank/PartnerBankClient.cs, PartnerBank/src/PartnerBank.Api/Distribution/SignedInstructions.cs, Gold/GoldDesk.cs.
2.  **Temporal gold-purchase workflow — exactly one ending, signal carries no authority**
    Workflow input is only the purchase number. paymentSettledSignal merely sets a flag; the next decision always re-reads the payment by Idempotency-Key. Allocation requires MarkPaid to match paymentRef + amount + settlement payee; cancellation is refused once Paid. Activities treat 400/403/409 as non-retryable. Worker identity is pinned (client\_id + no sub/act) on the internal endpoints.
    Evidence: src/Microservices/PartnerProducts/Worker/src/workflows.ts, activities.ts, API/Domain/GoldPurchase.cs (MarkPaid / EnsureCancellable), API/Authorization/DigitalGoldPolicies.cs (Worker policy).
3.  **Kafka SASL\_SSL + host-name check (B8) and payment-event filtering**
    Every .NET client goes through KafkaClientSecurity.Apply which fails closed without username/password/CA and sets SslEndpointIdentificationAlgorithm = Https. The Node worker does the same (ssl: true, ssl.endpoint.identification.algorithm: 'https', SCRAM-SHA-512). The listener only signals workflows for final outcomes whose payee matches the configured settlement account.
    Evidence: src/AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs, src/Microservices/PartnerProducts/Worker/src/payment-events.ts, config.ts.

---

The three Partner Bank patterns are correctly scoped to their stated purposes (A = full FAPI-style customer channel, not yet used by EWP; B = counter distribution with organisational authentication and non-repudiable instructions; C = short-lived PAR-style staff view handoff with no SSO). The code does not conflate them.