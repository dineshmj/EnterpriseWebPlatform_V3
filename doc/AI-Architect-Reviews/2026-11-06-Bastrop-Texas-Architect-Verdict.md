**Round 5 closing review of EWP V3 + Partner Bank**

I started from doc/AI-Architect-Reviews/Response-to-AI-Architect-Reviews.md § “Round 5 — the brief given to the reviewers”, then verified the listed change areas (FAPI 2.0 / DPoP for people and machines, crypto-shredding, contracts/tests, containers/supply-chain, Kubernetes/NetworkPolicies/cloud demo, BIAN /v2, fraud check + feature flags) against the code and the Partner Bank repository. I also cross-checked the residual-risk and “Known and out of scope” lists (Threat-Model §7 R-1..R-21, Partner Bank R1..R10, and the brief’s explicit exclusions).

**No new reportable findings.**

Every area the brief flagged as most useful was examined in source. The implementation matches the documents:

-   DPoP + private\_key\_jwt is required for staff web apps (PAR), Journey APIs (token exchange), Kafka subscribers, Partner Products Worker and every API’s token validation (cnf refusal + ConfigureDPoPTokensForScheme). Machine clients use ClientKeyStore (JWK only; private material never present at the IDP). Shared-secret usage is limited to the documented Grafana observability client (R-10 / demo).
-   Crypto-shredding encrypts under per-customer keys, blanks Outbox/Kafka after 7 days, erases via four-eyes in Customer Onboarding, then fan-out to KYC/Compliance/Accounts/Documents with idempotent internal endpoints and PersonalDataCopyErased confirmations. Known limits (backups, ACTIVE customers, payee name) are stated and not contradicted by code.
-   Pact contracts, WireMock provider checks, unit tests and Playwright 40-security / Digital Gold deviation runs exist and cover the saga couriers and Partner Products Worker.
-   Helm NetworkPolicies are default-deny + derived allow-lists (ingress from declared callers + Operations Journey; egress only DNS, declared callees, Postgres, Kafka, Temporal, OTLP, optional LaunchDarkly, Gateway). Images are multi-stage, non-root, digest-pinned, Trivy-gated.
-   BIAN /v2 mappers are validated against the published OpenAPI schemas; domain types do not leak.
-   Fraud scoring has a hard budget, circuit breaker, and cautious fallback (never blind ALLOW for new payee or high amount); feature-flag gate is fail-closed when the source is unavailable.
-   Partner Bank enforces mTLS + certificate-bound tokens on the external port, localhost/NetworkPolicy isolation on the internal port, PAR + PKCE for the window, and signed instructions for the three distributor flows. Residual risks R1–R10 remain as documented.

Nothing already answered in rounds 1–4, recorded as a residual risk, or listed under “Known and out of scope” is re-raised. No code-to-document contradiction was found that would reopen a closed item.

### Scores

-   **Architecture: 9.2 / 10**
-   **Security: 9.0 / 10**

### Three things that are sound

1.  **FAPI 2.0 end-to-end (people + machines)** — PAR + private\_key\_jwt + DPoP-bound tokens on every path; APIs refuse any token without cnf; machine clients hold no shared secret.
2.  **Crypto-shredding that preserves the audit trail** — per-customer keys, four-eyes decision, idempotent downstream erasure, confirmations that leave only non-personal audit entries, and explicit handling of the 7-day Kafka/Outbox window.
3.  **Cluster defence-in-depth** — per-component NetworkPolicies (ingress + derived egress), private CA / trust bundles, non-root distroless/chiseled images, and observability that itself authenticates through the IDP with strict role mapping.

### What would raise each score

-   **Architecture → 9.5+**: Close the remaining production-scale gaps that are already recorded (distributed rate limits / Redis or equivalent for R-14, end-of-day reconciliation, richer lending bounded contexts planned for V4) and expand Pact coverage to every courier field/status that a saga decision depends on.
-   **Security → 9.5+**: Move the residual risks that are accepted only for the PoC (R-9 full mTLS between services, R-10 production secret management, Partner Bank R10 federation alternative, backup/key-destruction procedures for crypto-shredding) into enforced production controls; add continuous audit-chain anchoring metrics/alerting that are already noted as production items.

The PoC is in excellent shape for the closing of V3. The residual work is correctly parked as production requirements rather than open defects.