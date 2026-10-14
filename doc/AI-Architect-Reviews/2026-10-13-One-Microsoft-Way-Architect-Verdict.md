# EWP V3 — Principal architect and security review

Review baseline: `EnterpriseWebPlatform_V3_20261013_160122.zip` · 

Today's date: 13-OCT-2026

## Executive assessment

This review uses the newly uploaded archive as the source of truth. I started by reading `ReadMe.txt`, `doc/Threat-Model.md`, and the project-level READMEs, then checked the implementation of the controls against the code rather than treating the documentation's claims as proof.

I also rechecked the previous review's major findings. Several important changes are present: payment saga replies are correlated with the current command, payment idempotency checks the original request details, the payment-network call is no longer made while holding the saga's database transaction, the Shell validates the expected iframe origin, and the document-storage and audit-checkpoint designs have been strengthened.

My preliminary verdict: EWP V3 has made substantial progress, but I would not yet grant an unconditional production go-ahead. The remaining concerns below are based on implementation evidence, not on the development-machine limitations you explicitly excluded.

Architecture

# 8.5 / 10

Strong patterns; remaining concerns are around end-to-end invariants and recoverability.

Security

# 7.5 / 10

Good control design; production assurance still needs some important gaps closed.

These are qualitative principal-review scores, not automated scan results or a certification against ASVS. I could inspect the source archive, but I have not claimed a successful .NET/TypeScript build, dynamic penetration test, or runtime verification.

I have respected your exclusions: committed development credentials and certificates, local-only infrastructure, simulators, demo identities, and the absence of automated tests are not being reported as findings in themselves. Where a design weakness would survive moving to production infrastructure, I have retained it.

## Findings requiring remediation

The findings are ordered by risk and likely impact. “High” means I would block a production go-ahead until the issue is fixed or an explicitly approved compensating control is demonstrated.

### EWP-01 — Operations MFA can fail open through configuration

High

Security

New / current archive

Why it matters: Operations exposes actions that change simulator behaviour, which is a privileged operational capability. The Operations Journey API only enforces recent MFA when its `mfaEnabled` option is true. Unlike the .NET APIs, I could not find a corresponding startup guard that requires MFA to be enabled outside Development.

Evidence

-   `src/Microservices/Operations/JourneyApi/src/configuration/journey-options.ts`, around line 56: `mfaEnabled` is set by comparing `OPERATIONS_MFA_ENABLED` to the string `'true'`; unset or misspelled values become `false`.

-   `src/Microservices/Operations/JourneyApi/src/auth/delegated-token.guard.ts`, lines 87–92: the step-up check runs only when `requirement.stepUp && this.options.mfaEnabled`.

-   Compare `src/Common/WebUtilities/Security/MfaStepUp.cs` and the startup calls to `RequireMfaOutsideDevelopment()` in the Payments, Accounts, Compliance and KYC APIs.

Failure scenario: Operations is deployed with the environment variable omitted or set incorrectly. A correctly delegated token belonging to a user with the required permission can then perform a step-up-protected operation without a recent MFA authentication.

Recommended fix — small: Require explicit MFA enablement at startup outside Development, validate the configured age, and fail startup if the configuration is missing or false. Make the IDP and all enforcing APIs agree on the MFA policy. Verify that a password-only token is rejected for the protected Operations action.

### EWP-02 — Audit integrity depends on protecting the checkpoint signing key and checkpoint store

High

Security / audit

Partly fixed since previous review

The new signed-checkpoint design is a meaningful improvement over a hash chain stored solely in the audit database. It still depends on the integrity and availability of the signing key and the external checkpoint files.

Evidence

-   `src/Microservices/Audit/API/Infrastructure/AuditAnchors.cs`: signed checkpoints and their verification.

-   `doc/Threat-Model.md`, section 7, residual risk R-3: the remaining production requirements explicitly include a key vault/HSM and immutable checkpoint storage administered independently of the database.

Failure scenario: An attacker with control of both the audit database and the checkpoint-signing capability can rewrite historical entries and create checkpoints that validate against the rewritten chain. If checkpoints are replaced or deleted without independent retention, the evidence of tampering can also be weakened.

Recommended fix — medium: Keep the private signing key outside the API process where practical, use a managed key service or HSM, and store checkpoints in immutable storage under a separate administrative trust boundary. Monitor missing or overdue checkpoints, not just invalid signatures. Establish an independently retained checkpoint history.

This is a production assurance gap, not a claim that the current hash-chain implementation is ineffective.

### EWP-03 — The Operations token-exchange chain needs production fail-closed validation

High

Security / delegated authorization

New / current archive

The IDP's token-exchange validator uses an explicit allow-list, checks the subject token and its caller, and preserves the original user's identity and authentication time. The Operations Journey API also checks the expected audience, access-token type, delegated actor, scope and permission.

The concern is not the general RFC 8693 approach. It is that the enforcement policy needs to be demonstrably fail-closed across every configuration and service boundary.

Evidence

-   `src/IDP/Security/TokenExchangeGrantValidator.cs`, lines 41–60 and 77–118: caller/token allow-list and delegated-token creation.

-   `src/Microservices/Operations/JourneyApi/src/auth/delegated-token.guard.ts`, lines 63–88: token validation and endpoint authorization.

-   `src/Microservices/Operations/JourneyApi/src/configuration/journey-options.ts`, around line 56: MFA configuration defaults to disabled unless explicitly set to `'true'`.

Failure scenario: The deployment has the expected OAuth clients and permissions configured, but the Operations-specific MFA option is missing. Token exchange continues to work, and authorization checks that do not depend on MFA still succeed; the step-up requirement is silently skipped.

Recommended fix — small to medium: Validate the whole Operations security configuration at startup, including MFA policy and token audience/caller identity. Keep the IDP's client allow-list as the authoritative exchange restriction. Add a configuration review that proves each protected operation has the intended scope, permission and step-up requirement. This overlaps EWP-01, but it is the broader configuration-validation requirement.

### EWP-04 — The payment saga's parked-message recovery must remain auditable and safe under replay

Medium

Architecture / operations

Previous finding partly resolved

The previous review found a gap in the ability to recover parked outbox messages. The current Threat Model says this is now addressed through the Payment Processing Monitor, with the same message ID, a reason, `workflow.reprocess`, recent MFA, and timeline recording.

That is the right direction. The remaining production concern is to ensure replay is a controlled business operation rather than merely a message-delivery operation.

Evidence

-   `doc/Threat-Model.md`, section 7, residual risk R-5: documents the re-send workflow and its intended controls.

-   `src/Microservices/Payments/API/Domain/Aggregates/PaymentSaga.cs`: persisted saga history and state transitions.

-   `src/Microservices/Payments/API/Application/Commands/RunDueSagaStep.cs`: network status enquiry and handling of unknown outcomes.

Failure scenario: An operator replays a parked command after its external effect has already occurred, or after the saga has progressed. If replay is not checked against current business state and the downstream idempotency contract, it could cause duplicate processing or leave the payment and external system inconsistent.

Recommended fix — medium: Verify the operator action checks the current saga state, that replay retains the original message identity, and that the consumer and external endpoint are idempotent. Make the monitor expose the last error, retry history, downstream status and the reason the message was parked. Keep replay and compensation separate from generic retry.

I have not classified the replay feature itself as broken: the archive's Threat Model documents the intended safeguards. The remaining production gate is to verify the full operator-to-outbox-to-consumer path and the downstream idempotency contract.

### EWP-05 — Operations API needs explicit abuse controls for privileged actions

Medium

Security / resilience

New / current archive

The Operations Journey API is a privileged control plane for changing simulator behaviour. I found authorization and step-up checks, but I could not establish from the inspected code that the mutating Journey API endpoints have their own effective per-user and per-client rate limits.

Evidence

-   `src/Microservices/Operations/JourneyApi/src/auth/delegated-token.guard.ts`: authenticates the delegated caller and checks permissions and optional step-up MFA.

-   `src/Microservices/Operations/BFF.Web/app/v1/operations/actions.ts`: exposes the server-side actions that invoke the Journey API.

-   `src/Microservices/Operations/JourneyApi/src/`: the Journey API's endpoint and guard implementation.

Failure scenario: A compromised authorized session or client repeatedly invokes a privileged operation. Authentication and authorization succeed on each request, but the API has no effective application-level limit to constrain the rate of operations.

Recommended fix — small to medium: Add explicit limits to state-changing Operations endpoints, keyed at least by subject and client, with a separate overall limit. Add monitoring and alerting for repeated state changes. Confirm whether an upstream limiter already covers these routes before implementing a second one; if it does, document and test that control.

### EWP-06 — Payment saga correctness still needs a production-grade reconciliation contract

Medium

Architecture / financial consistency

Previous finding partly resolved

The old design held a database transaction open during the external network call. That issue is fixed: the current runner claims work in a short transaction, calls the network without holding that transaction, and records the answer under a fresh lock/version check.

The remaining concern is the correctness contract for an ambiguous external outcome. The saga now uses a status enquiry, and unknown results leave funds reserved. That is safer than releasing funds blindly, but the production design must define what happens if the external system cannot determine the outcome for a prolonged period.

Evidence

-   `src/Microservices/Payments/API/Application/Commands/RunDueSagaStep.cs`, lines 124–139 and 160–231: status enquiry, unknown outcomes and version-checked recording.

-   `src/Microservices/Payments/API/Infrastructure/PaymentNetwork/PaymentNetworkClient.cs`, lines 87–106: status enquiry and translation of responses.

-   `src/Microservices/Payments/API/Domain/Aggregates/PaymentSaga.cs`, lines 61–63: unknown outcome handling and the rule against releasing funds without confirmation.

Failure scenario: The network accepted a payment, but the response was lost. Status enquiries remain unavailable or inconclusive. The saga correctly avoids releasing the funds, but the payment can remain unresolved and funds may stay reserved indefinitely unless an operational resolution path exists.

Recommended fix — medium: Define a maximum unresolved duration and a documented manual reconciliation process. The process must not release funds merely because a timeout expired. Require an authoritative network status or a controlled, independently evidenced decision. Track unresolved payments, alert on age, and record the resolution and approving actor in the audit trail.

This is a residual operational requirement rather than evidence that the current saga incorrectly releases funds.

## Earlier findings rechecked against the new archive

The following are the material changes I verified in the current source. “Resolved” here means the particular code defect identified in the earlier review is no longer present; it does not mean the whole subsystem is production-certified.

|
Earlier finding

 |

Current status

 |

Evidence

 |
| --- | --- | --- |
|

Payments saga could accept a stale reply from an earlier command attempt

 |

Resolved in code

 |

`PaymentSaga.cs`, lines 467–480, compares the reply's `CausationId` to `CurrentCommandId` and records ignored replies. The reply processor forwards `causationId` to the API.

 |
|

Payment initiation reused an idempotency key without checking the original payload

 |

Resolved in code

 |

`InitiatePayment.cs`, lines 92–114, checks the initiating user and normalized payment details.

 |
|

Payment-network HTTP call ran while a saga DB transaction was open

 |

Resolved in code

 |

`RunDueSagaStep.cs` separates claim, external call and outcome recording.

 |
|

Shell accepted the iframe's origin based on its first ready message

 |

Resolved in code

 |

`src/Shell/client-app/app/page.tsx` now maintains `expectedFrameOriginRef` from the selected menu item's `baseURL` and validates the ready message against it.

 |
|

Full account numbers were included in payment/account logs

 |

Partly fixed / appears resolved in inspected paths

 |

`InitiatePayment.cs` and `FundsCommand.cs` use `LogMasking.Account(...)`. A full repository-wide log-data audit would still be required before sign-off.

 |
|

KYC document storage lacked at-rest encryption

 |

Resolved in design/code path

 |

`doc/Threat-Model.md` R-2 identifies envelope encryption in `EncryptedDocumentStorage.cs` as implemented. The production key-management requirement remains.

 |
|

Audit hash chain had no external integrity anchor

 |

Partly fixed / design materially improved

 |

`AuditAnchors.cs` implements signed checkpoints. Independent signing-key and immutable-storage administration remain production requirements.

 |
|

No operator path to resend parked payment outbox messages

 |

Reported as resolved by current documentation

 |

`doc/Threat-Model.md` R-5 describes controlled resend with a reason, permission, recent MFA and timeline evidence. The complete live recovery path still needs runtime verification.

 |
|

Payment saga could release funds after an ambiguous network outcome

 |

Partly fixed

 |

`RunDueSagaStep.cs` queries the network and keeps funds reserved on an unknown status. Long-lived unresolved cases still need an operational resolution process.

 |
|

MFA step-up checked authentication method but not recency

 |

Resolved in .NET implementation

 |

The current Threat Model describes `MfaStepUp.cs` requiring `amr=mfa` and a recent `auth_time`. The Operations-specific configuration issue is EWP-01.

 |

I also verified that the current archive contains the newer Accounts, Payments, Audit, Notifications, Compliance and Operations contexts, along with the token-exchange implementation. This is a materially different codebase from the incomplete archive used for the previous review.

## What I would require before production approval

My next-gate checklist would be:

1.  Make Operations MFA fail closed. Require it outside Development and prove the protected operation rejects password-only authentication.

2.  Complete the audit trust boundary. Put checkpoint signing and immutable retention under independently administered controls.

3.  Prove payment reconciliation and recovery. Demonstrate that ambiguous outcomes cannot release funds, that unresolved payments generate alerts, and that parked-message replay cannot create a second financial effect.

4.  Confirm privileged-operation abuse controls. Establish effective rate limiting and monitoring for Operations state-changing endpoints.

5.  Run an end-to-end authorization review. Validate the subject, audience, delegated actor, branch restrictions, separation of duties, permissions and recent-MFA requirements across the newer Next.js BFF → NestJS Journey API → domain API paths.

6.  Run the actual build and runtime checks. I have not compiled this archive or exercised its browser and service flows in this review. A static source review cannot prove deployment configuration, runtime middleware ordering, cookie behaviour, database grants or Kafka ACLs are effective.

## Final ratings

Architecture

## 8.5 / 10

What would raise it by one point: demonstrate complete recovery for ambiguous payment outcomes and parked messages, with observable operational ownership and reconciliation.

Security

## 7.5 / 10

What would raise it by one point: enforce fail-closed MFA across Operations and all sensitive paths, complete independent audit checkpoint protection, and validate the delegated authorization model through runtime tests and deployment configuration.

Principal-review conclusion: the updated EWP V3 has stronger architecture and security controls than the version I reviewed previously. The stale-saga-reply, payload-idempotency, long-held transaction and iframe-origin defects appear fixed in the current source. I would still hold production approval until the Operations MFA configuration is fail-closed, audit checkpoints have their independent production trust boundary, and payment reconciliation and privileged-operation controls are demonstrated end to end.