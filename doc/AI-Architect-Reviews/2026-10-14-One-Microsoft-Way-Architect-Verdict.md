Today's date: 14-OCT-2026

## Round 3: Additional code-level checks

I also checked the checklist assertions that are most consequential if they are inaccurate. The Operations rate limiter is now present, and the missing-MFA configuration issue from round 2 is fixed: `assertSafeOutsideDevelopment()` rejects the Operations Journey API configuration when MFA is disabled outside Development. The previous review's EWP-01 is therefore resolved, not a current finding.

One qualification is important for the checklists: the Journey API rate limiter uses in-memory counters per process. That is acceptable for the single-instance PoC, but the checklist's stated limits are not automatically global limits across multiple instances. A production deployment that scales horizontally must use a shared limiter or enforce equivalent limits at a trusted gateway.

## EWP-07 — Journey API rate limits are process-local, not deployment-wide

Medium

Architecture / security

New finding

Evidence

-   `src/Microservices/Operations/JourneyApi/src/http/rate-limit.ts`: `RateLimiter` stores counters in a local `Map`; its comment explicitly says each instance counts independently.

-   `src/Microservices/Operations/JourneyApi/src/main.ts`: the overall rate limiter is instantiated in the API process.

-   `doc/Checklists/08-Journey-APIs-NestJS.md`, section 3: documents the overall and per-person limits without stating that they are per instance.

Failure scenario: The Operations Journey API is deployed with several replicas. A caller can distribute requests across those replicas and receive a multiple of the intended per-person change allowance. Restarting an instance also resets its counters.

Recommended fix — small to medium: Use a shared rate-limit store or enforce a suitably configured gateway limit. Keep the per-person-and-client key for sensitive actions, and separately limit unauthenticated traffic. Update the checklist to state whether limits are per instance or deployment-wide.

Status: New. This does not invalidate the single-instance PoC's current controls, but it matters when scaling the service.

## Checklist assertions that need careful interpretation

I did not find evidence sufficient to declare a broad checklist failure merely because an item is marked `[x]`. The most consequential claims I checked—Operations MFA configuration, deny-by-default API authorization, iframe origin validation, payment idempotency, and reconciliation controls—are supported by the current implementation.

There are, however, three qualifications worth recording:

-   Outbox recovery: the checklist correctly leaves parked Accounts and Compliance messages without an operator resend tool. This is an acknowledged operational gap, not an undisclosed defect. Those messages can require controlled intervention if they park.

-   Shared token fixtures: the NestJS and .NET token validators are implemented separately, and the checklist correctly marks shared fixtures as open. The parity checklist is a useful interim control, but it is not proof that both implementations will continue to accept and reject the same token shapes.

-   Database privileges: the table-privilege checklist explicitly leaves narrower domain-API grants as a hardening idea. I have not promoted this to a finding without evidence that a particular application login can modify another aggregate or bypass a domain invariant. The current per-context database separation and lack of shared databases are important protections.

## Earlier findings now verified as resolved

The following are the most important round-1 and round-2 findings that I consider resolved in the current archive:

-   Stale payment saga replies: the saga correlates reservation and settlement replies with the current command, while allowing release confirmations from earlier attempts to recover compensation.

-   Branch authorization for funds reservation: the initiating branch is carried to Accounts and checked against the authoritative account under a row lock.

-   Idempotency-key payload mismatch: reusing a key with different payment details returns a conflict.

-   MFA race conditions: TOTP steps and recovery codes use conditional database updates; failure counts and lockout are updated atomically.

-   Shell iframe origin trust: the expected origin comes from the selected menu item, and messages are checked against both the iframe window and that origin.

-   MFA fail-open configuration: the Operations Journey API requires explicit security configuration and refuses unsafe non-Development settings.

-   Payment saga transaction boundaries: the network call occurs outside the database transaction, with a subsequent version-checked outcome update.

-   Unknown payment outcomes: network status enquiry, age-based alerts and four-eyes reconciliation provide a defined route for unresolved payments.

-   Parked Payments outbox messages: controlled resend retains the message ID, requires permission, recent MFA and a reason, and records the operation.

-   Audit integrity: signed checkpoints strengthen the chain's tamper evidence. The independently administered key and immutable checkpoint store remain explicitly accepted production requirements.

-   Account-number log masking: the previously identified payment/account logging paths use the shared masking helper.

These are source-level findings. I have not independently executed the full journeys, compiled every project or tested the deployed configuration in this round.

## Final assessment

Architecture

# 8.5 / 10

To raise this by one point: complete the operational recovery story for parked messages across all contexts and make reconciliation, replay and failure ownership consistent and observable.

Security

# 8 / 10

To raise this by one point: establish deployment-wide abuse controls, finish cross-stack token-validation parity, and demonstrate the authorization and audit controls under realistic runtime conditions.

These scores exclude the PoC infractions you explicitly asked me to disregard. They are qualitative architectural judgments, not an ASVS certification or a penetration-test result.

Principal-review conclusion: Round 3 is a materially stronger codebase. The earlier critical design concerns around payment command correlation, branch enforcement, idempotency, transaction boundaries and iframe origin validation have been addressed in the source. I found one new, concrete scaling concern: the Journey API's rate limits are process-local. I would record that as a production hardening item, alongside the acknowledged parked-message recovery gaps, rather than treating the PoC as fundamentally unsound. I would still require a build, runtime security verification and a deployment-level authorization review before granting production approval.