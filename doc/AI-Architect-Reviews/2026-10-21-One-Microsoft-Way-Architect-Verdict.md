Here is the complete Round 4 verdict, using both uploaded repositories and respecting your rule not to repeat accepted risks or planned production work.

# EWP V3 — Round 4 architectural verdict

Review baseline: EWP V3 and EWP V3 Partner Bank archives uploaded on October 21, 2026.

## Findings

I did not establish a new, reportable Critical, High or Medium defect in the code paths I inspected. I would not invent findings to fill a report.

|
ID

 |

Title

 |

Severity

 |

Type

 |

Status

 |
| --- | --- | --- | --- | --- |
|

—

 |

No newly confirmed defect

 |

—

 |

—

 |

No finding issued on the inspected paths

 |

This is a qualified result, not a claim that every file in both repositories has been exhaustively verified. The remaining checks and limitations are described below.

## Partner Bank interaction patterns

### A — Customer sign-in: PAR \+ JAR \+ RAR

Assessment: design is consistent with the stated purpose.

The Partner Bank's architecture explicitly assigns customer authentication and consent to the Partner Bank. It requires EWP to use a signed authorization request, PAR, PKCE, and certificate-bound tokens. That is distinct from trusting EWP's IDP to authenticate the customer.

Evidence:

-   Partner Bank `doc/Architecture.md`, decisions D3–D5.

-   Partner Bank `doc/Threat-Model.md`, threats T13–T17.

-   EWP response document, Round 4 section “Three ways EWP deals with the Partner Bank.”

I have not independently exercised every PAR/JAR/RAR validation branch in a running environment, so this is a source-and-design assessment, not protocol conformance certification.

### B — Distributor counter: mTLS \+ bound token \+ signed instruction

Assessment: the trust model is appropriate for the stated distributor arrangement.

The design distinguishes the authenticated EWP organisation from the staff member acting within EWP. The Partner Bank relies on the distribution agreement and EWP's checks rather than pretending to authenticate EWP staff itself.

The signed-instruction validator in Partner Bank `src/PartnerBank.Api/Distribution/SignedInstructions.cs` checks the signing key, permitted algorithm, token type, issuer, audience, lifetime and instruction ID (lines 41–77). The one-time handling is implemented separately in the instruction-processing path.

The reliance on EWP to verify the customer's signed paper instruction is explicitly documented as Partner Bank Threat Model R9. I have therefore not raised it again as a missing control.

### C — “View at the Partner Bank”: one-time staff-view handoff

Assessment: the implementation matches its stated purpose, with the documented limitations.

Evidence: Partner Bank `src/PartnerBank.Api/Distribution/StaffViews.cs`:

-   Lines 26–27: pending requests and used instruction IDs are tracked separately.

-   Lines 34–39: a signed instruction cannot be pushed twice.

-   Lines 41–44: the request reference is generated using 32 random bytes and given a 60-second lifetime.

-   Lines 47–60: redemption removes the pending request atomically, so a reference cannot be redeemed twice.

The in-memory storage and the fact that the Partner Bank trusts EWP's assertion of the staff member's identity are already documented as residual risk R10. I have not reported either again.

## Temporal workflow and Kafka

The workflow implementation contains several important protections.

-   `src/Microservices/PartnerProducts/Worker/src/workflows.ts`, lines 55–60, makes the payment-settled signal data-less: the signal wakes the workflow but does not dictate the outcome.

-   Lines 64–85 re-read payment status and only allocate on `COMPLETED`; non-paid terminal states or an expired wait lead to cancellation.

-   `src/Microservices/PartnerProducts/Worker/src/payment-events.ts`, lines 41–57, configures SASL\_SSL, SCRAM-SHA-512, CA configuration where supplied, and hostname verification.

-   Lines 84–96 validate the destination settlement account, find the corresponding purchase, and signal the relevant workflow.

The five-minute polling fallback is documented in the Temporal checklist and Threat Model R16. Therefore, a failed signal delivery does not itself prove that the workflow will miss a payment outcome.

The Temporal development server's lack of TLS and authorization is also an explicitly accepted PoC limitation under R16; I have not reported it again.

## Earlier findings and checklist claims

I rechecked the specific areas called out in the Round 4 instructions. I did not find evidence sufficient to reopen an earlier finding on the inspected paths.

In particular, I did not treat these as new defects:

-   Per-instance rate limits, recorded under EWP Threat Model R14.

-   Temporal frontend security, recorded under R16.

-   Staff-view identity reliance and in-memory state, recorded under Partner Bank R10.

-   Reliance on EWP's verification of customer paper instructions, recorded under Partner Bank R9.

-   Planned customer self-service/FAPI 2.0 work that is explicitly outside the current EWP channel.

I also did not interpret an acknowledged production requirement as a current PoC defect.

## What I checked and found sound

1.  Signed distributor instructions: Partner Bank `src/PartnerBank.Api/Distribution/SignedInstructions.cs`, lines 41–77. The validator pins the trusted signing key and validates signature-related claims, audience, issuer, expiry and instruction ID.

2.  One-time staff-view redemption: Partner Bank `src/PartnerBank.Api/Distribution/StaffViews.cs`, lines 34–60. The reference is random, short-lived and atomically redeemed once.

3.  Payment outcome handling: EWP `src/Microservices/PartnerProducts/Worker/src/workflows.ts`, lines 64–85. The workflow checks authoritative payment status rather than trusting the signal payload.

## Scores

Architecture

Clear separation between the distributor, partner bank, and workflow worker.

# 8.7 / 10

Security

Strong, explicit controls in the inspected paths; full protocol and runtime verification remains outstanding.

# 8.5 / 10

These scores exclude the accepted PoC risks you specified. They are qualitative judgments, not certification results.

Final verdict: I found no new confirmed defect in the inspected Round 4 paths that meets your reporting criteria. The Partner Bank's three interaction patterns have distinct, coherent trust models, and the signed-instruction, one-time handoff and payment-workflow controls I checked are consistent with those models. This is a positive source-level review, but it is not a blanket production approval: I have not independently executed the complete protocol journeys, built every project or performed runtime penetration testing.

What does it take, to elevate the security and architecture soundness to 9 or above?

For EWP V3, I would aim for 9+/10 by strengthening the guarantees that cross service boundaries, proving that security controls hold under failure, and making recovery independently auditable—rather than adding more architectural patterns.

Your platform already demonstrates substantial architecture: bounded contexts, separate databases, outbox/inbox messaging, choreography and orchestration sagas, Temporal workflows, delegated OAuth token exchange, branch-aware authorization, separation of duties, MFA step-up, and signed audit checkpoints.

The next step is to demonstrate that these controls work together when things go wrong.

# 1\. What would elevate EWP V3 to 9+/10?

Priority 1 — Establish end-to-end identity and authorization assurance

Security

-   Prove the complete EWP → Partner Bank authentication flow, including mTLS client identity, PAR/JAR validation, PKCE, issuer/audience validation and the resulting authenticated customer identity.

-   Demonstrate that EWP staff identity, customer identity, partner-bank identity and service identity cannot be confused or substituted.

-   Verify branch restrictions, separation of duties, permissions and recent-MFA requirements at the authoritative API—not just in the BFF or UI.

-   Fail closed when required security configuration is absent or inconsistent.

What earns the score: a documented trust model backed by negative tests and observable authorization decisions.

Priority 2 — Prove financial workflow correctness under failure

Architecture + security

-   Exercise Temporal workflow restarts, worker loss, duplicate signals, delayed events and activity retries.

-   Test Kafka redelivery, out-of-order messages, consumer restarts and parked-message recovery.

-   Demonstrate that a payment cannot be settled twice, that compensation is idempotent, and that ambiguous network outcomes do not release reserved funds incorrectly.

-   Define how long-running unresolved payments are detected, reconciled and escalated to an authorized operator.

What earns the score: evidence that the workflow reaches a valid terminal state—or remains safely unresolved with an actionable recovery path.

Priority 3 — Make audit evidence independently verifiable

Security + governance

-   Verify that signed audit checkpoints detect changes, truncation and missing checkpoint intervals.

-   Keep production signing keys and immutable checkpoint retention under independent administrative control.

-   Record who initiated, approved, executed and reconciled a privileged operation.

-   Correlate the relevant workflow, business operation, messages and audit records without treating a correlation ID as proof of authenticity.

What earns the score: an auditor can reconstruct a sensitive business operation and independently verify that its evidence has not been silently altered.

Priority 4 — Demonstrate operational resilience

Architecture

-   Establish deployment-wide rate limits for sensitive operations.

-   Alert on stuck workflows, unresolved payments, parked messages, consumer lag and failed audit checkpoints.

-   Verify retry limits, backoff, timeout budgets and recovery ownership.

-   Document operational runbooks with safe replay, reconciliation and compensation procedures.

What earns the score: a failure is detected, bounded and recoverable without an operator improvising a financial correction.

# 2\. The highest-value improvement: a cross-boundary assurance suite

If I could prioritize only one major engineering investment, I would create an EWP Security and Workflow Assurance Suite.

Not simply a collection of unit tests. It should verify the properties that matter across the entire architecture.

|
Test scenario

 |

Required outcome

 |
| --- | --- |
|

Customer changes the requested identity or audience

 |

Authentication or authorization fails

 |
|

EWP staff member tries to access another branch's customer

 |

Authoritative API denies access

 |
|

Token exchange is attempted by an unregistered client

 |

IDP rejects the exchange

 |
|

The same payment command is delivered repeatedly

 |

No duplicate financial effect

 |
|

A stale saga reply arrives after a retry

 |

It cannot incorrectly advance the current workflow

 |
|

Temporal worker stops during an activity

 |

Workflow recovers without duplicating the external effect

 |
|

Kafka publishes a duplicate or delayed event

 |

Consumer remains idempotent; invalid state transitions are rejected

 |
|

Payment outcome remains unknown

 |

Funds are not released without authoritative evidence

 |
|

An audit entry or checkpoint is altered

 |

Integrity verification detects the change

 |
|

Required security configuration is removed

 |

The relevant service fails startup or denies the operation

 |

For the Partner Bank, include all three interaction patterns separately. Pattern A must establish customer authentication through the Partner Bank's accepted trust model; Pattern B must validate the distributor's signed instruction; Pattern C must enforce one-time redemption of the staff-view handoff.

Each test should assert not just an HTTP status, but also the persisted state, emitted events, authorization decision and audit evidence where applicable.

# 3\. What specifically raises the security score?

I would measure progress against these concrete gates.

### Security assurance checklist

0 of 5 verified

Cross-bank identity and token validation

Positive and negative tests for issuer, audience, client identity, token exchange, request signing and identity mapping.

Authoritative authorization

Branch, role, resource ownership, separation-of-duties and MFA enforcement demonstrated at the domain/API boundary.

Financial and privileged-action audit

Complete actor attribution, approval evidence, tamper detection and independently retained checkpoints.

Abuse resistance

Effective limits on sensitive operations, bounded resource use and alerts for suspicious repeated actions.

Security verification evidence

Threat-model-to-test traceability, dependency and configuration scanning, plus independent penetration testing.

Use this as a planning aid; ticking a box here does not verify the code or environment.

A 9+ security assessment requires more than finding no obvious vulnerabilities. It requires evidence that important controls resist bypass, that failures are handled safely, and that the evidence can be independently checked.

# 4\. What specifically raises the architecture score?

For architecture, I would focus on four outcomes:

1.  Explicit ownership: Every business invariant has one authoritative owner. Temporal coordinates long-running work; it does not silently become the owner of payment or account state.

2.  Reliable integration: The interaction between domain transactions, outbox/inbox, Kafka and Temporal has defined delivery, idempotency, ordering and recovery semantics.

3.  Bounded failure: Each dependency has sensible timeouts, retry budgets, backoff and concurrency limits. Retries do not amplify a failing downstream service.

4.  Operational closure: Every nonterminal business state has an owner, a detection mechanism and a documented resolution path.

The key architecture question is not whether the system uses enough patterns. It is whether each pattern has a clear responsibility and whether the combined system remains correct when those patterns interact.

# 5\. My suggested order of work

Phase 1 — Close documented gaps

Reconcile the checklists and Threat Models against current code. Resolve the remaining security and operational gaps that apply to your target deployment.

Phase 2 — Build the assurance suite

Prioritize cross-bank trust, authoritative authorization, payment idempotency, Temporal recovery and audit integrity. Run negative tests as well as successful journeys.

Phase 3 — Verify the production configuration

Validate key management, immutable audit retention, Kafka authorization, rate limiting, identity federation and runtime settings in a production-like environment.

Phase 4 — Independent review

Have another reviewer test the trust boundaries and financial workflows, including the failure paths. Record evidence for each high-risk threat and each production checklist item.

## My final recommendation

I would not pursue a particular score by adding more technology. EWP V3 already has a strong architectural foundation.

I would pursue demonstrable correctness, fail-closed security, and recoverability. If the cross-bank identity flow, financial saga recovery, audit integrity and operational controls all pass meaningful negative and failure-injection tests—and the production deployment satisfies its documented requirements—then a 9+/10 assessment becomes defensible.

Even then, a score is a summary judgment, not a substitute for formal security assurance or a bank's production risk acceptance.