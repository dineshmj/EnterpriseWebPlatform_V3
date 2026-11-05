I’ve read the Round 5 brief first and am treating it as the review boundary. In particular, I will not re-raise the accepted PoC risks, the Partner Bank’s R1–R10, or the previously answered findings. I’m checking the new claims against the repository code rather than treating the brief or test descriptions as proof by themselves.

The main Round 5 review areas are DPoP and machine authentication, crypto-shredding and downstream erasure, the new contract coverage, deployment boundaries, and the BIAN, fraud-check and feature-flag changes.The first code checks support several of the intended safeguards: the Journey API validates DPoP proofs against the request method, URL, token hash and bound key, and rejects replayed proof IDs; the fraud fallback sends first-time payees and payments at or above its configured threshold for review rather than silently declining or allowing them; and downstream erasure handlers are written to tolerate redelivery. I’m checking the surrounding authorization, persistence and deployment paths before deciding whether those controls hold end to end.## Round 5 — closing verdict

No new reportable findings identified

I found no new Critical, High, Medium or Low defect that meets your evidence threshold in the areas examined. I am not assigning finding IDs simply to fill the report.

That is a positive source-review result, not a claim that the repositories are production-certified. I could inspect the uploaded source, documentation and test definitions, but this environment does not have the .NET SDK, pnpm or Helm available. I therefore could not independently execute the .NET test suite, Node test suite, chart validation or cloud deployment. The cloud-demo results and test outcomes described in the brief remain repository-reported evidence, not executions I reproduced during this review.

### What I checked

|
Area

 |

Source-level result

 |
| --- | --- |
|

DPoP and machine identity

 |

The Partner Products Journey API validates proof type and algorithm, method, URL, issue time, access-token hash, key thumbprint and one-time `jti`. Its replay cache fails closed when full. `src\Microservices\PartnerProducts\JourneyApi\src\auth\dpop-proof.verifier.ts:7–14, 40–76, 94–108`.

 |
|

DPoP key separation

 |

The Journey API loads its own encrypted PKCS#8 key, requires P-256 and creates a fresh proof ID for each request. `src\Microservices\PartnerProducts\JourneyApi\src\auth\dpop-key.service.ts:15–33, 41–58`.

 |
|

Partner Products authorization

 |

The Domain API requires configured client-ID pins at startup and uses them in its authorization model. Its customer reads are branch-scoped in the database query. `src\Microservices\PartnerProducts\API\Program.cs:36–43`; `src\Microservices\PartnerProducts\API\Controllers\DigitalGoldController.cs:70–91`.

 |
|

Fraud-check fallback

 |

A scoring timeout or unavailable provider invokes explicit fallback rules. A first payment to a payee or an amount at or above the fallback limit goes to review; the fallback does not manufacture a decline decision. `src\Microservices\Payments\API\Application\Fraud\FraudCheck.cs:23–46, 76–107`.

 |
|

Payment idempotency

 |

A repeated key is accepted only for the same person and same normalized payment details; conflicting details produce a domain conflict. `src\Microservices\Payments\API\Application\Commands\InitiatePayment.cs:116–142`.

 |
|

Crypto-shredding primitives

 |

Customer data uses AES-256-GCM with a per-customer data key, wrapped under a master key; the customer number is associated data. The blind index uses a separate HMAC key, and the code explicitly recognises that the index must also be cleared during erasure. `src\Microservices\CustomerOnboarding\API\Infrastructure\Privacy\PersonalDataCipher.cs:28–39, 47–61, 64–90, 118–130`.

 |
|

Downstream erasure

 |

Accounts and KYC process erasure in a transaction and delete the customer's local key; Documents Management blanks the document name and retries deletion of all stored content before the subscriber confirms. `src\Microservices\Accounts\API\Application\Commands\ErasePersonalData.cs:15–54`; `src\Microservices\CustomerKyc\API\Application\Commands\ErasePersonalData\ErasePersonalDataCommandHandler.cs:15–54`; `src\Microservices\DocumentsManagement\API\Application\Documents\Commands\ErasePersonalData\ErasePersonalDataCommandHandler.cs:12–53`.

 |
|

Feature flags

 |

The implementation documents and follows the separation between feature availability and authorization. A flag can switch a feature off; it does not grant permission. `src\Common\WebUtilities\FeatureFlags\EwpFeatureFlags.cs:18–29, 72–90`.

 |

The DPoP replay cache being process-local, production key custody, immutable audit storage, service-to-service mTLS, and the other items explicitly listed as residual risks are not new findings in this review. I have not counted them again.

## Scores

Architecture

# 9.1 / 10

A coherent, domain-oriented platform with explicit ownership, asynchronous workflow boundaries, contracts, and a credible deployment model.

Security

# 9.0 / 10

Strong identity binding, deny-by-default authorization, branch-scoped access, replay protection, and explicit financial and privacy controls. The remaining deductions are predominantly assurance and production-hardening matters.

These are my architectural review scores for the supplied PoC repositories, not certification scores or a statement that every production requirement has been met.

## Three things that are particularly sound

1\. Identity is treated as a chain of proof, not just a token. The design distinguishes staff, service and partner identities; DPoP binds tokens to keys; and the resource APIs enforce their own authorization rather than relying on UI visibility or upstream checks.

2\. Financial and asynchronous workflows have explicit failure behaviour. The payment saga, Temporal workflow, idempotency rules, data-less payment signal and recovery paths are designed around duplicate delivery, delayed answers and uncertain outcomes. That is much stronger than a happy-path-only implementation.

3\. Privacy is addressed across service boundaries. Crypto-shredding is not treated as merely deleting a customer row. The design accounts for downstream copies, blind indexes, document content, Outbox/Kafka retention and the audit evidence needed to demonstrate the erasure decision.

## What would raise the scores further

Architecture

-   Complete the remaining cross-context Pact contracts, particularly where a consumer depends on a provider's event fields or status transitions. The current contract README itself identifies consumers that have not yet been added.

-   Demonstrate recovery in the deployed multi-instance environment, not just through isolated tests: database outages, worker restarts, broker outages, duplicate events and partial erasure.

-   Carry the same architectural discipline into V4: preserve bounded-context ownership as lending and PayTo introduce more complex business relationships.

Security

-   Complete the production controls already identified in the threat model: HSM/key-vault custody and rotation, independently administered immutable audit checkpoints, deployment-wide rate limits, security-event monitoring and production service identity controls.

-   Run an independent penetration test against the actual deployed trust boundaries, especially token exchange, DPoP, staff authorization, Partner Bank instructions, the one-time staff-view handoff and financial workflow transitions.

-   Add repeatable production-configuration assurance: verify that a deployment cannot silently start with missing pins, weak secrets, unintended network paths or relaxed authentication settings.

## Closing assessment

For a closing V3 review, I would not recommend adding more architectural patterns. The platform already has a substantial set of patterns and controls. The next improvement should be evidence: repeatable tests, operational demonstrations, independent security assessment and production-grade key and infrastructure controls.

I would close Round 5 with no new findings, while retaining the documented production requirements as explicit go-live gates. That is a defensible conclusion for this source-level review; it is not a production approval.