## Review update: the correct source is now under review

This archive contains 1,760 entries and includes the Payments and Accounts APIs, the separate Audit microservice, Notifications/SignalR, the IDP's TOTP MFA implementation, and the Kafka saga components. This is the version I needed.

I've already found a more consequential issue than a missing security header: the Payments saga appears to discard the reply's `CausationId`, even though the Accounts service publishes it to identify the command that caused the reply. The saga's transition guard checks the current step and status, but not whether the reply belongs to the current command attempt.

That creates a plausible financial correctness failure: after a reservation times out and is retried, a delayed failure reply from the earlier attempt could be accepted even if the newer attempt successfully reserved the funds. The saga could mark the payment rejected without releasing the funds. I’m treating this as a high-priority finding and checking the surrounding transaction and compensation paths before finalizing the report.

# EnterpriseWebPlatform V3 — Enterprise Security & Architecture Review

Review target: `EnterpriseWebPlatform_V3-main(4).zip` · Static source inspection · 10 October 2026

Dinesh, the corrected archive gives me a much more complete picture. You have built considerably more than a conventional microservices demonstration: there are distinct business contexts, asynchronous workflow processing, identity and access controls, payment orchestration, audit-chain integrity, and notification delivery.

My assessment is that the architectural foundations are strong, but several implementation details need to be corrected before I would regard this as production-ready banking software. The most important concerns are not cosmetic. They affect payment correctness, authorization boundaries, MFA enforcement, and the integrity of the evidence your system records.

One limitation up front: this is a static code review. The environment available to me does not have the .NET SDK or pnpm, so I could not compile the solution, execute the Playwright suite, or run dependency vulnerability scans. I distinguish source-confirmed defects from risks requiring runtime verification below.

## 1\. Executive assessment

## Architecture

## 8/10

Strong domain boundaries, orchestration, transactional messaging, and explicit authorization policies. Deductions for a few workflow correctness defects and scaling trade-offs.

## Security

## 6.5/10

Good security engineering is visible, but the findings below include payment authorization, browser trust, MFA configuration, and sensitive-data handling concerns.

## Production readiness

## 5.5/10

Provisional rating: runtime verification, financial failure testing, deployment hardening, and operational controls remain unproven or incomplete.

These are qualitative ratings of the source reviewed, not certification results or measurements from a penetration test.

## 2\. Findings that should be addressed first

I am using the following severity scale:

-   Critical: credible path to catastrophic compromise or loss.

-   High: material security or financial-correctness risk that should block production release.

-   Medium: significant weakness, resilience defect, or production-readiness gap.

-   Low: limited impact, hardening, or maintainability concern.

HIGH

### Finding 1 — A stale Payments Saga reply can be accepted as the current command's reply

Files: `Payments/API/Domain/Aggregates/PaymentSaga.cs`, `Payments/API/Application/Commands/HandleSagaReply.cs`, `AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/Processing/PaymentsSagaReplyProcessor.cs`

This is the most important workflow-correctness defect I found.

Accounts publishes a reply whose envelope includes a `CausationId` identifying the command that produced it. The Payments reply processor reads that field, but does not forward it to the Payments API. The API's `SagaReplyCommand` therefore has no command-causation identifier to validate.

In `PaymentSaga.cs`, `Expecting(...)` checks the current step and saga status. Despite its comment describing late replies after a resend, it does not compare the reply's causation identifier with `CurrentCommandId`.

Failure scenario

1.  Payments sends `ReserveFunds` attempt 1.

2.  The reply times out, and the saga sends attempt 2 with a new command ID.

3.  Attempt 2 reserves the funds successfully, but its reply is delayed.

4.  A delayed `FundsReservationFailed` reply from attempt 1 arrives first.

5.  The saga is still at `ReserveFunds`, so the stale failure can be accepted and the payment rejected without compensation.

The result can be a held balance that the payment workflow no longer intends to settle or release.

Required fix: carry `CausationId` through the reply subscriber, request DTO, command handler, and domain method. Validate that it matches the current command ID before applying the reply. A mismatch should be recorded as stale and handled idempotently. Add tests for out-of-order success/failure replies, retries, and compensation.

HIGH

### Finding 2 — Payment initiation relies too heavily on the UI's branch-filtered account lookup

Files: `Payments/API/Controllers/PaymentsController.cs`, `Accounts/API/Controllers/PaymentAccountsController.cs`, `Accounts/API/Application/Commands/FundsCommand.cs`

The Accounts lookup endpoint is explicitly branch-filtered. However, the Payments initiation endpoint accepts `CustomerNumber`, source BSB and account number from the request. It derives the payment's branch from the staff token, then sends the supplied account details to Accounts.

Accounts checks whether the specified account can reserve the requested amount and whether the customer number matches the account. But the funds command does not carry the initiating employee's branch, and the authoritative reservation path does not re-enforce the branch restriction of the lookup endpoint.

Consequently, the API does not establish that the staff member is entitled to initiate a payment from the selected account under the same branch policy enforced by the lookup screen. Calling the Payments API directly can bypass the UI's search restriction if the caller knows or obtains another customer's account details.

This is especially important because the seeded `customer_service_agent` role has `payment.initiate`, and payments below the configured approval threshold can proceed without a second officer's approval.

Required fix: enforce the account/customer authorization policy on the authoritative command path, not just the lookup endpoint. Carry or independently derive the relevant branch and customer relationship, verify it in Accounts under the appropriate transaction/lock, and reject out-of-scope accounts. If production policy requires customer consent or a mandate for assisted transfers, model and verify that explicitly; a staff permission is not proof of customer authorization.

HIGH

### Finding 3 — The Shell still trusts an iframe's origin based on the first message

File: `src/Shell/client-app/app/page.tsx`, around `handleFrameMessage`

The Shell verifies `event.source === iframe.contentWindow`, but when it receives `BSS_MFE_READY`, it assigns `event.origin` to `currentFrameOriginRef.current` and sends the workspace context to that origin.

Checking the source window is useful, but it is not sufficient. A window can navigate to another origin while retaining the same `WindowProxy` identity. If the frame can be navigated to an untrusted origin, that page can announce itself as ready and receive context.

The Shell also uses the saved origin to relay notifications into the frame. The same trust decision therefore affects both workspace context and notification delivery.

Required fix: define an explicit allowlist mapping each MFE to its trusted origin. Validate both `event.source` and `event.origin` before accepting any message. Derive the expected origin from trusted configuration, not the incoming message. Apply the same validation to navigation permission replies and other cross-frame protocols. Keep CSP as a second layer of defence.

HIGH

### Finding 4 — MFA and step-up enforcement are disabled by the shipped default configuration

Files: `src/IDP/appsettings.json` and `appsettings.json` for Accounts, Compliance, CustomerKyc and Payments

The TOTP implementation is real and includes encrypted authenticator secrets, hashed recovery codes, lockout integration, replay protection, and authentication-method claims. That is good work.

However, `Mfa:Enabled` is `false` in the base configuration for the IDP and the four APIs that enforce step-up. The shared `MfaStepUpAuthorizationHandler` explicitly allows the requirement whenever this setting is false.

Therefore, MFA is implemented but not enabled by default, and the APIs independently control whether sensitive actions require an MFA claim. A deployment that enables MFA at the IDP but misses one API's configuration can have inconsistent step-up enforcement.

Required fix: make production configuration fail startup if MFA is not explicitly enabled where policy requires it. Validate that the IDP and all step-up APIs use a consistent policy. Add deployment smoke tests that prove a password-only token is rejected for sensitive actions. Development can remain MFA-off deliberately, but that must not be an implicit production default.

HIGH

### Finding 5 — The Payments step runner holds a database transaction open during an external network call

Files: `Payments/API/Application/Commands/RunDueSagaStep.cs`, `Payments/API/Infrastructure/Persistence/PaymentsRepositories.cs`

`RunDueSagaStepCommandHandler.HandleAsync` begins a database transaction, selects and locks a due saga using `FOR UPDATE SKIP LOCKED`, then calls `SendToNetworkAsync` before saving and committing. That method makes an HTTP request to the external payment network, with resilience timeouts configured up to 20 seconds for the overall call.

This holds the saga row lock and transaction open across external I/O. Under slow network conditions or a growing backlog, it can cause lock contention, connection-pool pressure, long-running transactions, and reduced throughput.

The external call is also a side effect that cannot be rolled back by PostgreSQL. The configured idempotency key mitigates duplicate payment risk only if the external provider durably honours that key and returns the same result on retries.

Required fix: persist a durable transition/claim before making the external call, execute the call outside the database transaction, then persist its outcome and next outbox command in a short transaction. Model timeout/unknown outcome explicitly and reconcile with the network before compensating where the real provider's semantics require it.

## 3\. Other high-priority authorization and financial controls

### Finding 6 — Auditor-only access can be bypassed by combined permissions

File: `Payments/API/Authorization/PaymentsAuthorization.cs`

The Payments authorization handler intends to prevent auditors from reading the Payments screens directly; auditors should access payment details through the Audit Journey API using a delegated token.

The check is based on:

```
granted.All(p => p == AuditReadPermission)
```

If a caller has `payment.history.view` and another permission accepted by `PaymentView`, the `All(...)` condition is false. The handler can then authorize the caller directly. `ReadScopeOf(...)` also treats `payment.history.view` as an all-branches permission.

This is a concrete policy weakness when users can have multiple roles—a capability the identity schema permits.

Remediation: remove the audit-only permission from the general `PaymentView` permission list. Keep the Audit Journey API's delegated-token policy separate. Add regression tests for an auditor alone, an auditor combined with a payments role, and an operations administrator.

### Finding 7 — Payment idempotency does not compare the repeated request with the original payload

File: `Payments/API/Application/Commands/InitiatePayment.cs`

A repeated `PaymentRef` (the `Idempotency-Key`) from the same user returns the existing payment without comparing the submitted customer, account, amount, payee, or reference against the original request.

This avoids duplicate payment creation, but it means that accidental or malicious reuse of the same key with a different request is silently treated as a retry.

Remediation: persist a canonical request fingerprint alongside the idempotency key. Return the original result only when the fingerprint matches; otherwise return `409 Conflict`. Preserve the existing unique constraint and concurrent-insert handling.

### Finding 8 — MFA replay protection is vulnerable to concurrent verification races

File: `src/IDP/Security/MfaService.cs` and the `UserMfa` / `UserMfaRecoveryCode` persistence model

The code correctly records `LastUsedTimeStep` and marks recovery codes as used. But the verification flow reads those records, checks their state in application code, and saves the result without an evident concurrency token or atomic conditional update.

Two simultaneous requests could both observe the same TOTP step as unused—or the same recovery code as unused—and both succeed before either update becomes visible.

Remediation: perform the verification state transition atomically. For example, use a conditional update on the TOTP time step and check the affected row count; consume recovery codes with a single conditional `UPDATE ... WHERE used_at IS NULL`. Add concurrent-request tests. Apply equivalent concurrency protection to lockout counters.

### Finding 9 — Sensitive bank account numbers are written to application logs

Files: `Payments/API/Application/Commands/InitiatePayment.cs`, `Accounts/API/Application/Commands/FundsCommand.cs`

The Payments initiation log formats the `BankAccountRef` values, whose `ToString()` contains the BSB and full account number. The Accounts funds-command log also includes BSB and account number.

These are financial identifiers that can flow into centralized log storage, observability platforms, exports, and incident archives with different access and retention controls from the banking databases.

Remediation: remove full account numbers from routine logs. Use a payment reference, trace ID, and a masked account identifier where operationally necessary. Review existing log retention and access policy, and ensure exceptions and tracing do not reintroduce the same values.

### Finding 10 — Approval and MFA configuration should fail closed

File: `Payments/API/Program.cs`

The approval threshold falls back to `1,000` when `Payments:ApprovalThreshold` is absent. The approval-limit parser treats a blank value as no limit. The current configuration intentionally gives clearance level 5 an unlimited approval limit, but the same representation can also arise from a missing or accidentally blank setting.

This is a dangerous configuration convention for financial controls: an omitted setting can silently become a permissive policy.

Remediation: require explicit production values for the approval threshold and every clearance level. Represent an unlimited limit with a deliberate, validated value rather than an empty string. At startup, validate the configured policy against an approved policy schema and reject unexpected or missing levels.

## 4\. Audit microservice — good foundation, but don't call the chain tamper-proof

The Audit microservice is one of the stronger architectural additions. I found:

-   A separate API and database.

-   A Kafka subscriber that records business events.

-   A hash chain over audit entries.

-   An append-only database trigger.

-   A least-privilege design in which the application database user is intended to have `SELECT` and `INSERT`, not `UPDATE`, `DELETE`, or DDL.

-   Audit reads that themselves create `ACCESS` entries.

-   A scheduled integrity monitor and an explicit integrity endpoint.

These are valuable controls. The following limitations remain.

### Finding 11 — The hash chain has no independent trust anchor

Files: `Audit/API/Domain/AuditEntry.cs`, `Audit/API/Infrastructure/AuditTrailAppender.cs`, `Audit/API/Application/AuditChainVerifier.cs`, `Audit/API/AuditDb/EwpAuditDb.sql`

The verifier can detect an isolated modification, deletion, insertion, or reordering that breaks the chain. The append-only trigger and the application's restricted database role also make ordinary tampering harder.

However, an administrator or attacker with sufficient database privileges could disable the trigger or otherwise rewrite the complete chain and recompute all subsequent hashes. If no trusted copy of a prior chain head exists outside that database, a complete rewrite may verify successfully.

Remediation: periodically sign and export chain checkpoints to a separate security boundary—such as an independently administered immutable store or a separately controlled signing service. Document the threat model accurately: the current design is tamper-evident against many classes of database modification, but not proof against a privileged attacker capable of rewriting the entire store.

### Finding 12 — A single global audit chain serializes all writes

The fixed PostgreSQL advisory lock in `AuditTrailAppender` serializes every append across the entire audit trail. That is simple and provides a deterministic global sequence, but it also creates a single write-throughput bottleneck.

The verifier additionally walks the entire chain in batches. As the trail grows, verification time and database load will grow with it.

Remediation: measure append throughput and verification duration under realistic load. If scale demands it, consider partitioned chains with independently signed checkpoints, or another design that preserves tamper evidence without requiring every event to wait for a single global lock. Do not change this merely for theoretical performance; benchmark it first.

### Finding 13 — The audit evidence depends on producer and broker trust

The audit mapper takes the actor and initiator fields from event envelopes. This is reasonable when events are produced by trusted services, but those fields are not independently authenticated by the audit service.

A compromised producer with permission to publish to a business topic could potentially emit an event with misleading actor metadata. The payload hash proves what the audit service received, not who genuinely originated the event.

Remediation: enforce Kafka ACLs per producer and topic, validate the expected source/event combinations, and retain broker provenance. For stronger assurance, consider signed event envelopes or a separately authenticated producer identity in the audit record.

## 5\. MFA and identity — implemented, but configuration and freshness matter

Beyond the findings above, I would address these items before production.

### Finding 14 — MFA is checked as a historical authentication method, not a recent step-up

The `MfaStepUpAuthorizationHandler` checks whether the token's `amr` claim contains `mfa`. It does not visibly check how recently MFA occurred.

That is sufficient if the policy means “the user authenticated with MFA at some point in the current authentication context.” It is not sufficient if the policy means “the user must have performed a fresh second-factor challenge shortly before approving a payment or retrying a release.”

Remediation: define the freshness requirement for sensitive actions. If fresh authentication is required, use an OIDC flow that requests appropriate authentication context or maximum age, and validate the resulting authentication time/context. A sign-out-and-sign-in instruction is a workaround, not an ideal high-assurance step-up experience.

### Finding 15 — TOTP enrolment failures do not appear to share the normal MFA lockout path

`MfaService.VerifyAsync` increments failed attempts, but `ConfirmEnrolmentAsync` returns `null` for an incorrect enrolment code without visibly incrementing the same counter.

The login rate limiter and short-lived pending cookie mitigate this, but the enrolment path should be reviewed for consistent throttling and lockout behaviour.

Remediation: enforce both per-account and per-IP throttling during enrolment, with a bounded number of attempts and clear recovery behaviour.

### Finding 16 — MFA Data Protection and signing keys remain deployment-critical

The IDP's persistent Data Protection setup is a good sign: the implementation is designed to survive restarts and multiple instances. But the key ring's encryption and availability are critical because it protects pending MFA state and encrypted authenticator secrets.

Remediation: verify key protection, backup, access separation, rotation, disaster recovery, and multi-instance consistency in the actual deployment. Ensure production signing certificates and secrets are supplied externally, and that the development signing path cannot be selected in production.

## 6\. SignalR and notifications

The Notifications microservice is correctly separated from the Shell. The hub derives audiences from the authenticated user's claims, exposes no client-callable group-joining method, and closes connections when the access token expires. Notifications are persisted before live push, so a failed push does not destroy the stored notification.

Those are strong design choices.

### Remaining concerns

-   Hub resource consumption: the common rate limiter is configured for `/v1`, whereas the hub is at `/hubs/notifications`. I did not find equivalent connection-count limits in the hub code. Authenticated clients could potentially create excessive connections. Add connection quotas, infrastructure-level limits, and monitoring for connection churn.

-   Origin checks: the Shell's WebSocket route has an origin check, but its fallback accepts a request without `Origin` when `Sec-Fetch-Site` is also absent. Modern browsers normally supply an origin for WebSocket connections; however, non-browser clients can omit both. Keep this in mind when assessing the threat model, and rely on token validation and network controls as well.

-   Notification privacy: notification text can include payee names, amounts, customer identifiers, and rejection reasons. Treat notification storage and delivery as a sensitive data surface, and apply retention and access controls.

-   Deep links: the notification target is stored as JSON. Keep the client-side route selection restricted to known MFEs and allowed relative paths; never turn an event-supplied target into an arbitrary URL.

## 7\. Saga architecture and distributed reliability

The Payments implementation has a good foundation:

-   Saga state is persisted.

-   The API uses a transactional outbox.

-   Accounts has a transactional inbox and a durable funds-hold model.

-   Command delivery is designed for at-least-once semantics.

-   The reply subscriber has an explicit machine identity.

-   The saga records history and exposes processing/backlog information.

-   The external network client uses timeouts, retries, and a circuit breaker.

-   Compensation failure becomes a visible operational state rather than being silently discarded.

That is a much more credible orchestration design than a long chain of synchronous API calls.

There are still important operational details to settle.

### Finding 17 — Outbox parking needs a formal recovery process

The Payments publisher parks messages after bounded attempts. That prevents a poison message from being retried forever, but a parked command can block later messages for the same aggregate because publication preserves per-aggregate order.

That may be the correct safety behaviour. The missing production control is a safe, observable redrive process.

Remediation: provide an operator-controlled re-drive mechanism that preserves message identity and audit history, records who re-drove the message and why, and checks whether the downstream side effect already occurred. Do not solve this by skipping a failed message without understanding its business meaning.

### Finding 18 — Delivery ordering is not a global business-order guarantee

The outbox preserves order within an aggregate stream. Kafka preserves order within a partition. Neither gives you a universal total order across different topics and services.

The authoritative ordering rules must remain in the domain state machine, using command causation, expected states, idempotency, and explicit barriers. The stale-reply finding above is exactly the kind of defect that durable state alone does not prevent if messages are not correlated to the current command.

### Finding 19 — Payment idempotency depends on the external provider's contract

The same `PaymentRef` is sent as the external `Idempotency-Key`, which is the correct general idea. But production safety depends on the provider guaranteeing that repeated requests with that key cannot produce multiple payments, including after a timeout, provider failover, or a long delay.

Remediation: document and test the real provider's idempotency retention window and response-replay semantics. Add a reconciliation/status-enquiry process for “unknown outcome” cases before releasing funds.

### Finding 20 — In-process background workers need explicit operational ownership

The Payments API hosts both the saga step runner and outbox relay. This is a reasonable choice for a PoC and makes the ownership model clear. It does, however, couple API availability and background workflow execution.

Before scaling, decide whether these workers should remain in the API deployment or become independently scalable worker deployments. Either model is valid, provided the database locks, health checks, idempotency, and ownership rules are preserved.

## 8\. Credentials, infrastructure, and deployment configuration

The current archive contains development-only credentials in configuration files and a development TLS private key/certificate pair under the KYC BFF. I did not find the earlier IDP `tempkey.jwk` in this archive, which is an improvement.

I do not treat the development values as evidence that production credentials have been compromised. But the repository/distribution hygiene should be improved.

### Required actions

1.  Remove private development keys and certificates from distributable archives; generate them locally and exclude them from source control where possible.

2.  Move deployment secrets to a managed secret store or protected environment configuration.

3.  Make production startup fail if connection strings, signing certificates, client secrets, Kafka credentials, or external API credentials are missing or still contain development defaults.

4.  Keep Kafka TLS/SASL and database access controls enabled in production. The local development configuration uses localhost endpoints and development credentials; that must not become a deployment template by accident.

5.  Tighten `AllowedHosts`, reverse-proxy trust, forwarded-header configuration, CSP settings, and public health/metrics exposure for the actual hosting topology.

6.  Run automated secret scanning and dependency vulnerability scanning in CI.

### Finding 21 — Database scripts are destructive reset scripts, not production migrations

The database creation scripts start by dropping tables, including the Payments schema and audit trail. The comments explicitly describe recreating the databases for development.

That is acceptable for a controlled local reset. It is not acceptable as an ordinary production deployment mechanism.

Remediation: keep these scripts clearly labelled as destructive development/bootstrap scripts. Introduce versioned migrations and deployment safeguards for production schema changes. Require explicit approval and backup/recovery plans for destructive operations.

## 9\. Testing and verification — the biggest confidence gap

The archive contains a Playwright test suite, but the test files present are limited to:

-   Readiness/smoke checks

-   Sign-in

-   Customer Onboarding journeys

-   Browser security headers

-   IDP session revocation after sign-out

I could not find .NET unit-test projects for the APIs and domain logic. The test README itself says .NET unit and adapter tests are still to come.

For a system with financial operations, concurrent workflows, TOTP verification, ABAC/ReBAC, and append-only audit logic, that is a significant gap.

### Tests I would require before production approval

### Payments correctness

-   Stale `FundsReservationFailed` after a successful retry cannot reject a payment incorrectly.

-   Duplicate and concurrent `Idempotency-Key` requests cannot create duplicate payments; a different payload with the same key is rejected.

-   External acceptance followed by a process crash is reconciled without a second payment.

-   Two officers cannot concurrently approve/reject the same payment.

-   Compensation is idempotent and eventually reaches a reconciled terminal state.

### Authorization and identity

-   A staff member cannot initiate a payment from an out-of-scope customer's account by bypassing the lookup screen.

-   An auditor with a second role cannot bypass the Audit Journey API restriction.

-   Password-only tokens are rejected for all actions requiring MFA when the policy is enabled.

-   Concurrent reuse of a TOTP code or recovery code cannot authenticate twice.

-   A malicious iframe origin cannot receive workspace context or notifications.

### Audit, Kafka, and resilience

-   Duplicate Kafka delivery, poison messages, dead-lettering, and redrive are exercised.

-   Outbox publication followed by a crash does not produce duplicate business effects.

-   Audit chain tampering, missing entries, and concurrent appends are tested.

-   SignalR connections cannot receive another user's or branch's notifications.

-   Database and Kafka outages, restarts, and backlog recovery are tested under load.

The most important tests should be automated at the domain, application, and database integration levels—not only through the browser.

## 10\. Recommended remediation order

I would not begin by refactoring CSS or reorganizing components. I would tackle the risks in this order:

|
Priority

 |

Work item

 |

Reason

 |
| --- | --- | --- |
|

P0

 |

Fix Payments reply causation validation

 |

Prevent incorrect payment state transitions from stale replies.

 |
|

P0

 |

Enforce account/branch authorization on payment initiation

 |

Prevent bypass of UI-only account restrictions.

 |
|

P0

 |

Validate the Shell's MFE origin against a static allowlist

 |

Prevent cross-origin workspace/notification disclosure.

 |
|

P0

 |

Make production MFA and approval configuration fail closed

 |

Prevent security controls from being silently disabled by configuration.

 |
|

P1

 |

Move the external network call outside the database transaction

 |

Reduce contention and improve saga reliability.

 |
|

P1

 |

Fix TOTP/recovery-code concurrency

 |

Ensure one-time codes are truly single-use under concurrency.

 |
|

P1

 |

Remove full account numbers from logs

 |

Reduce financial data exposure.

 |
|

P1

 |

Separate auditor-only permissions from normal Payments read policies

 |

Prevent combined-role authorization bypass.

 |
|

P1

 |

Add .NET unit/integration tests for payment and saga invariants

 |

Make future changes safer and provide regression evidence.

 |
|

P1

 |

Add formal outbox recovery and external-payment reconciliation

 |

Ensure failures can be resolved without unsafe manual database changes.

 |
|

P2

 |

Add externally anchored audit checkpoints

 |

Strengthen the audit trail against privileged tampering.

 |
|

P2

 |

Harden production secrets, host/CSP configuration, and SignalR quotas

 |

Reduce deployment and resource-exhaustion risk.

 |
|

P2

 |

Benchmark the audit chain and worker/backlog behaviour

 |

Establish a realistic capacity and recovery envelope.

 |

P0 here means “fix before production approval,” not that the application has already been exploited.

## Final verdict

You have made strong architectural decisions: independent bounded contexts, server-side BFFs, explicit scopes and policies, transactional messaging, a persisted Payments saga, compensating actions, a separate audit service, and a server-authorized notification channel. These are not superficial patterns; several are implemented in a thoughtful way.

But my merciless conclusion is this:

The architecture is ahead of the current verification and hardening. The stale saga-reply issue and the payment initiation authorization boundary are the two findings I would prioritize above everything else. They concern whether the system performs the correct financial action for the right actor—not merely whether the code follows a pattern.

The Shell origin issue, MFA defaults, and concurrent one-time-code handling come next. The audit chain is a good design, but its assurance must be described accurately and supported by independent trust anchors if it is expected to withstand privileged tampering.

I would call this a strong, advanced architectural PoC with important production blockers, not production-ready banking software yet. That is not a dismissal of the work; it is the distinction I would want a security reviewer to make before this platform is trusted with real money or regulated customer data.

I have not changed your code in this review. My recommendation is to resolve the P0 findings first, add regression tests for them, and then repeat the review against the patched archive.