# Payments API

The Payments bounded context, and the home of the platform's **orchestrated saga**. A staff member captures a payment for a customer (assisted channel); the API records it and starts a `PaymentSaga`, which carries the payment out in the background:

```text
reserve funds (Accounts) → [approval by a payments officer, above the tier] → send to the payment network → settle funds (Accounts) → COMPLETED
```

If something fails after the funds were reserved, the saga **compensates**: it asks Accounts to release them. Business requirements: [Payments-Requirements.md](../../../../doc/Requirements/Payments-Requirements.md). The pattern and the comparison with onboarding's choreography: [Saga plan §2](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments).

URL: `https://payments-api.dev.localhost:44488`. Database: `EwpPaymentsDb` (user `ewp_payments_api`). Kafka user: `ewp-payments-api`.

## Who does what

| Piece | Where | Role |
|---|---|---|
| `PaymentSaga` (Domain/Aggregates) | Payments API | **The orchestrator.** A persisted state machine: the ONLY place where workflow decisions are made. |
| `Payment` (Domain/Aggregates) | Payments API | The business state people see (RESERVING_FUNDS, COMPLETED, FAILED, ...). Changed only by its saga. |
| `SagaStepRunner` | Payments API (background service) | The saga's timer: calls the payment network when a send is due, and wakes sagas whose reply did not come in time. Decides nothing. |
| Outbox publisher | Payments API (background service) | Sends the saga's commands to `accounts.commands` and the payments' outcomes to `payments.payment.events`. |
| Internal replies endpoint | Payments API | `POST /internal/v1/payment-sagas/replies`: the reply courier hands Accounts' replies to the saga. |
| [AccountsCommandSubscriber](../../../AsyncWorkflows/Subscribers/Accounts/AccountsCommandSubscriber/README.md) | console worker (Accounts) | Courier: `accounts.commands` → Accounts API. |
| [PaymentsSagaReplySubscriber](../../../AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/README.md) | console worker (Payments) | Courier: `accounts.funds.replies` → this API. |
| [Payment Network Simulator](../../../Simulators/PaymentNetworkSimulator/README.md) | external | Called **directly over HTTP** by the step runner (Idempotency-Key, timeout, retry, circuit breaker). |

Nothing waits in memory. `POST /v1/payments` saves the payment, the saga and the first command in **one transaction** and answers **202 Accepted** in milliseconds. Each later step is a short, separate piece of work (a reply arrived, a timer is due) that loads the saga row, decides, and saves the new state with the next command in one transaction. A restart resumes from the saved step.

The payment network call is the exception: no transaction stays open across it. The step runner **claims** the attempt (short transaction: the saga's timer moves on by a 60-second lease, so no other instance takes it), **calls** the network with no lock held, then **records** the answer (short transaction, applied only if the saga's Version is still the claimed one). If the process dies mid-call, the lease runs out and the attempt is repeated - safely, because the PaymentRef is the network's Idempotency-Key.

## The saga

```text
RESERVE_FUNDS ─FundsReserved─► (AWAIT_APPROVAL ─approved─►) SEND_TO_NETWORK ─accepted─► SETTLE_FUNDS ─FundsSettled─► DONE   payment COMPLETED
RESERVE_FUNDS ─FundsReservationFailed─► DONE                                                                     payment REJECTED (nothing to undo)
SEND_TO_NETWORK ─refused─► RELEASE_FUNDS ─FundsReleased─► DONE                                                   payment FAILED (funds released)
SEND_TO_NETWORK ─no answer after 4 tries─► CONFIRM_WITH_NETWORK (status enquiry, repeated until the network answers)
    ─sent─► SETTLE_FUNDS ─FundsSettled─► DONE                                                                    payment COMPLETED (the answer was only lost)
    ─never received / refused─► RELEASE_FUNDS ─FundsReleased─► DONE                                              payment FAILED (funds released)
AWAIT_APPROVAL ─rejected by the officer─► RELEASE_FUNDS ─FundsReleased─► DONE                                     payment REJECTED (funds released)
RELEASE_FUNDS ─no reply after N tries─► STUCK                                                                    payment COMPENSATION_FAILED
```

| Situation | What the saga does |
|---|---|
| No reply from Accounts in time | Sends the command again (Accounts treats the same PaymentRef idempotently); timeout doubles 30 s → 5 min. |
| Reservation never confirmed (5 tries) | Releases, to be sure, and the payment ends FAILED. |
| Settlement not confirmed | Keeps trying: the money already left through the network, so settlement is never abandoned. |
| Release not confirmed (5 tries) | Stops (STUCK) and the payment is **COMPENSATION_FAILED**: it never claims an undo that did not happen. A late `FundsReleased` still resolves it; operations can also **Retry release** (a fresh `ReleaseFunds` with fresh attempts) from the payment page. |
| Network unavailable | Retries with back-off (5 s doubling, 4 attempts). The PaymentRef is the network's Idempotency-Key, so a retry can never pay twice. After the last attempt the outcome is **unknown** - the payment may have gone out with only the answer lost - so nothing is released yet (next row). |
| Outcome unknown (reconciliation) | Asks the network by Idempotency-Key whether it sent the payment (`GET /v1/payments/by-idempotency-key/{key}`): **sent** → settles (the money left; releasing it would pay twice); **never received** or **refused** → releases. While the network cannot answer that either, the funds stay reserved and the enquiry is repeated with back-off (up to 60 s) for as long as it takes - the saga shows as overdue. Demonstrate it with the Payment Network simulator's **LosingAnswers** behaviour (Operations → Platform Health). |
| Network refuses (HTTP 422) | Compensates at once. |
| Late or duplicate reply | Recorded as REPLY_IGNORED in the timeline; changes nothing. |

Every line above is written to `payment_saga_history`, returned as the payment's **timeline** by `GET /v1/payments/{id}`.

Limits are configuration (`Payments:ApprovalThreshold`, default 1,000.00; `Payments:Saga:*`). In **Development** the saga waits 10 s for a reply (doubling to 60 s) and tries 3 times, so a failed compensation shows within about a minute; production values are in `appsettings.json` (30 s → 5 min, 5 tries). `/health/ready` turns **Degraded** when a saga is overdue by 2 minutes or a compensation failed.

## API

| Endpoint | Policy | Purpose |
|---|---|---|
| `POST /v1/payments` | `payments.write` + `payment.initiate` + branch | Start a payment. Header **`Idempotency-Key`** (a GUID, one per payment form): the same key returns the same payment (200 instead of 202); another person's key is refused (409). |
| `GET /v1/payments?status=&pageNumber=&pageSize=` | `payments.read` + `payment.view` or `payment.initiate` | The branch's payments, newest first. |
| `GET /v1/payments/{id}` | as above | The payment, its saga (step, status, attempts, next check, last error, WorkflowId) and the timeline. Another branch's payment is 404. |
| `GET /v1/payments/by-number/{paymentNumber}` | as above | The same, found by the payment number - how other contexts refer to a payment (e.g. the Audit Journey API, acting for an auditor by token exchange). |
| `GET /v1/payments/policy` | `payments.read` + `payment.view` or `payment.initiate` | The limits the screens explain up front: currency, approval threshold, maximum amount, reference length, and what the caller's clearance may approve. |
| `POST /v1/payments/{id}/approve` | `payments.write` + `payment.approve` + branch | A payments officer approves a PENDING_APPROVAL payment: never one they started (SoD, 403), within their clearance's limit (`Payments:ApprovalLimits`: 3 → 10,000, 4 → 100,000, 5 → any; 403 above it). The saga resumes and sends it. |
| `POST /v1/payments/{id}/reject` | `payments.write` + `payment.reject` + branch | Rejects with remarks (required): the saga releases the funds and the payment ends REJECTED (`APPROVAL_REJECTED`). |
| `GET /v1/payments/bsb/{bsb}` | as above | BSB directory (answered by the payment network): bank, branch, state; 404 when unknown. |
| `POST /v1/payments/payee-confirmations` | as above | Confirmation of Payee: `MATCH`, `CLOSE_MATCH` (with the name the bank holds) or `NO_MATCH`. A warning for the staff member, never a decision. |
| `GET /v1/payments/processing` | `payments.read` + view permission | The Payment Processing Monitor: counts (compensation failed, overdue, retrying, awaiting approval, running) and every unfinished saga, most urgent first. |
| `POST /v1/payments/{id}/retry-release` | `payments.write` + `workflow.retry` (operations) | Retry the release of a COMPENSATION_FAILED payment; 409 when nothing is stuck. Any branch. |
| `POST /internal/v1/payment-sagas/replies` | pinned M2M client of the PaymentsSagaReplySubscriber | Replies from Accounts. |

ABAC: a staff member sees and starts payments for their own branch only; **operations** (`workflow.view`), whose work is not branch-bound, read every branch. **Auditors** (`payment.history.view`) read every branch too, but only when the token's acting client is the Audit Journey API (token exchange): the Audit Trail's "where it stands now" works, the Payments screens refuse them (`PaymentsStaffAuthorizationHandler`). Accounts additionally checks that the paying account belongs to the customer named in the payment.

## Try it

Through the screens: as **sophie.cs**, Payments → New Payment ([Payments BFF + MFE](../BFF.Web/README.md)). Or directly against the API with Bruno: a token for sophie.cs with the scopes `payments.read payments.write`, and a customer's account from EwpAccountsDb (`SELECT customer_number, bsb, account_number, balance, held_amount FROM accounts;`).

```http
POST https://payments-api.dev.localhost:44488/v1/payments
Authorization: Bearer {{token}}
Idempotency-Key: 6f1d6a52-2f0e-4c1a-9d0b-1a2b3c4d5e6f      (a NEW GUID per payment)
Content-Type: application/json

{
  "customerNumber": "<the account holder's customer number>",
  "fromBsb": "062-000",
  "fromAccountNumber": "<their account number>",
  "payeeName": "Jane Citizen",
  "toBsb": "063-019",
  "toAccountNumber": "12345678",
  "amount": 250.00,
  "reference": "Rent October"
}
```

→ `202 Accepted { paymentId, paymentNumber, status: "RESERVING_FUNDS" }`. Then `GET /v1/payments/{paymentId}` a second later: `COMPLETED`, with the timeline. In EwpAccountsDb the balance dropped by 250.00 and `funds_holds` shows the hold `SETTLED`.

| Try | Expect |
|---|---|
| Same request again (same Idempotency-Key) | 200 with the SAME payment - no second payment. |
| Amount above the available balance | REJECTED, `INSUFFICIENT_FUNDS`, nothing to undo. |
| `toBsb` starting with `999` | The network refuses: COMPENSATING → **FAILED**, hold `RELEASED`, balance unchanged. |
| Simulator `Down`, then a payment | Network retries in the timeline, then compensation → FAILED. |
| Stop the AccountsCommandSubscriber, then a payment | TIMEOUT / resend lines (10 s, 20 s, 40 s in Development); after 3 tries the reservation is given up and released - which is not answered either - so after about 2.5 minutes the payment is **COMPENSATION_FAILED**. **daniel.ops** sees it in the Payment Processing Monitor, opens it and presses **Retry release**; start the subscriber again and the release is confirmed: payment **FAILED**, funds released. |
| Amount above 1,000.00 | Stops at **PENDING_APPROVAL**; the branch's payments officers are notified. emily.payments (Payment Approvals) approves → sent and completed, or rejects → funds released, REJECTED. Sophie cannot decide her own payment. |

## Messages

Commands (to `accounts.commands`, key = PaymentRef): `ReserveFunds`, `SettleFunds`, `ReleaseFunds`. Events (to `payments.payment.events`): `PaymentApprovalRequired`, `PaymentCompleted`, `PaymentRejected`, `PaymentFailed`, `PaymentCompensationFailed`, `PaymentUnconfirmedByNetwork`, and the manual reconciliation's `PaymentNetworkResolutionProposed` / `Approved` / `Rejected`. All in the standard envelope (WorkflowId / CorrelationId per payment; CausationId = the message that led to it). Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Strict access-token validation: issuer, audience, lifetime, signature against the IDP's keys, and only `typ: at+jwt` access tokens (RFC 9068); original claim names kept (`MapInboundClaims = false`) | A token issued for another API, an ID token or an expired token would be accepted | Token confusion and replay across APIs (OWASP API2 Broken Authentication) | `AddJwtBearer` in [Program.cs](Program.cs) |
| 2 | Deny by default, the same in every domain API: every controller needs an authenticated caller on top of its own policy, and any other endpoint without a policy needs one too (fallback policy); only health and metrics are explicitly anonymous | A new endpoint added without an attribute would be public | Broken function-level authorization (OWASP API5) | `MapEwpApiControllers`, `DenyAnonymousByDefault` in [Program.cs](Program.cs); [ApiEndpointAuthorization.cs](../../../Common/WebUtilities/Security/ApiEndpointAuthorization.cs) |
| 3 | Step-up: payment approval, rejection, "Retry release", resending a parked Outbox message and manual reconciliation require a RECENT two-step sign-in (`amr` contains `mfa` and `auth_time` at most `Mfa:StepUpMaxAgeMinutes`, default 15, old) while `Mfa:Enabled` is on; outside Development the API refuses to start unless it is on | A stolen password alone would be enough to approve or reject; a session left open since a morning sign-in would still approve; a forgotten setting would silently switch the step-up off in production | Account takeover leading to fraudulent decisions; insecure defaults (OWASP A05) | [MfaStepUp.cs](../../../Common/WebUtilities/Security/MfaStepUp.cs), `stepUpPolicies` and `RequireMfaOutsideDevelopment` in [Program.cs](Program.cs) |
| 4 | Branch scope; operations staff read every branch but cannot decide; the auditor's permission counts only through the Audit Journey API (acting client checked) - also for a person who holds it next to another role | A clerk could see another branch's payments; an auditor could use the operational screens unrecorded; a clerk who is also an auditor would read every branch on the Payments screens | BOLA (OWASP API1); unrecorded access; privilege combination | `EffectivePermissionsOf`, `ReadScopeOf`, `ActingClientOf` in [PaymentsAuthorization.cs](Authorization/PaymentsAuthorization.cs) |
| 5 | Approval tier and approval limits by clearance (AUD 10,000 / 100,000 / `Unlimited`), checked in the saga aggregate; both fail closed - a missing threshold, or a blank or unreadable limit, stops the start-up | Any payments officer could approve any amount; a missing or blank setting would silently become a default or "no limit" | Fraudulent high-value payments; insecure defaults (OWASP A05) | `ApprovalLimits` in [PaymentDetails.cs](Domain/ValueObjects/PaymentDetails.cs), [PaymentSaga.cs](Domain/Aggregates/PaymentSaga.cs), start-up checks in [Program.cs](Program.cs) |
| 6 | The initiator never approves their own payment | One person could create and approve a payment | Self-approval (maker = checker) | `EnsureMayDecide` in [Payment.cs](Domain/Aggregates/Payment.cs) |
| 7 | Payment network calls with an API key and an `Idempotency-Key` | A retried send could pay twice | Duplicated money movement | [PaymentNetworkClient.cs](Infrastructure/PaymentNetwork/PaymentNetworkClient.cs) |
| 8 | Internal endpoints accept only the one machine client meant to call them (`client_id` pinned, not just the scope) | Any service holding the scope could post fake workflow steps (e.g. a forged "funds held" message) | Service impersonation and spoofed workflow events | Policy `PaymentsSagaReplySubscriberWrite` in [Program.cs](Program.cs); [InternalPaymentSagaRepliesController.cs](Controllers/InternalPaymentSagaRepliesController.cs) |
| 9 | Problem responses without internals (a `traceId` only; details are logged on the server) | Stack traces, SQL or exception messages would reach the caller | Information disclosure that helps an attacker (CWE-209, OWASP A05) | [ApiExceptionHandler.cs](Controllers/ApiExceptionHandler.cs) |
| 10 | Per-caller rate limits (person, machine client, IP), 429 with `Retry-After`; the subscribers' internal endpoints too (machine-client budget); counted per instance (R-14) | One caller, or a stolen token, could flood the API for everybody | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs), `AddEwpRateLimiting` in [Program.cs](Program.cs) |
| 11 | Own least-privilege database user `ewp_payments_api`: its own database only, no DDL | A compromised API or leaked connection string would expose other contexts' data or let the schema be changed | Lateral movement and blast radius of a breach | [EwpServiceDbUsers.sql](../../../../db/EwpServiceDbUsers.sql) |
| 12 | HTTPS only (HSTS outside Development) and host filtering (`AllowedHosts`) | Tokens could travel over plain HTTP; a forged `Host` header could poison generated links | Interception (OWASP A02) and host-header attacks | `UseHsts` in [Program.cs](Program.cs); `AllowedHosts` in [appsettings.Development.json](appsettings.Development.json) |
| 13 | An `Idempotency-Key` belongs to one person and one payment: the same key with other details (customer, accounts, payee, amount, reference) is refused with 409 | A reused key with a changed amount or payee would be silently answered with the first payment, hiding the second request | Request confusion; tampered retries | `Repeat`, `IsSamePayment` in [InitiatePayment.cs](Application/Commands/InitiatePayment.cs) |
| 14 | The saga accepts a reservation or settlement reply only as the answer to its current command (the reply's `CausationId` must be the command's `MessageId`); a release confirmation is accepted from any attempt | A reply to an earlier attempt could move the payment, out of order | Out-of-order and stale workflow messages changing a payment | `Expecting` in [PaymentSaga.cs](Domain/Aggregates/PaymentSaga.cs), [InternalPaymentSagaRepliesController.cs](Controllers/InternalPaymentSagaRepliesController.cs) |
| 15 | The funds commands carry the initiator's branch, so Accounts reserves money only on an account of that branch | Calling the API directly with another branch's account details would move that customer's money | BOLA through the payment command path (OWASP API1) | `FundsCommandIssuedDomainEvent` in [PaymentsDomainEvents.cs](Domain/Events/PaymentsDomainEvents.cs); the check in the Accounts API's [Account.cs](../../Accounts/API/Domain/Aggregates/Account.cs) |
| 16 | No full account numbers in logs: the BSB and the last four digits only | Account numbers would spread into log shippers and observability platforms with other access and retention rules | Exposure of financial identifiers (OWASP A09, privacy) | [LogMasking.cs](../../../Common/Observability/LogMasking.cs), [InitiatePayment.cs](Application/Commands/InitiatePayment.cs) |
| 17 | Parked Outbox messages: only operations see them (`outbox.view`, never the payload) and resend one (`workflow.reprocess`, a required reason of 10-500 characters, recent MFA); the SAME message id goes back to the relay, so consumers' Inboxes drop a copy that did arrive; who resent it, when and why is kept on the message and on the payment's timeline | A parked message (e.g. Kafka down for long) would block the payment for good, or be fixed by hand in the database - unrecorded, and possibly as a new message that a consumer acts on twice | Silent loss of workflow messages; unaudited privileged changes; duplicated money movement | [ResendParkedMessage.cs](Application/Commands/ResendParkedMessage.cs), [ParkedOutbox.cs](Infrastructure/Messaging/ParkedOutbox.cs), [OutboxController.cs](Controllers/OutboxController.cs), `RecordMessageResent` in [PaymentSaga.cs](Domain/Aggregates/PaymentSaga.cs) |
| 18 | The payment network's address is checked at start-up: an absolute URL without credentials; outside Development https, a host listed in `PaymentNetwork:PermittedHosts`, and no loopback, private or link-local address; its client follows no redirects | A wrong or tampered address (e.g. a cloud metadata endpoint) would receive the API key and customer data; a redirect could move the call elsewhere | Server-side request forgery by configuration (CWE-918) | `ExternalEndpoint` in [Program.cs](Program.cs); [ExternalEndpoint.cs](../../../Common/WebUtilities/Security/ExternalEndpoint.cs) |
| 19 | A payment the network has not confirmed for `Payments:Saga:UnconfirmedAlertMinutes` is an alert (readiness Degraded, Processing Monitor); only then may operations resolve it by hand, with FOUR EYES: one person (`payment.reconcile`, recent MFA) proposes SENT - with the network's reference - or NOT_SENT, with evidence; ANOTHER person (never the proposer, never the initiator) approves or rejects. A time-out alone never releases funds, the enquiry goes on and the network's own answer supersedes a proposal; each step is on the timeline and published, so the audit trail names both people | Funds could stay reserved for ever, or one person could settle or release them on a guess - releasing money the network did pay out | Financial loss from an unknown outcome; insider fraud; missing audit evidence | `ProposeNetworkResolution`, `DecideNetworkResolution` in [PaymentSaga.cs](Domain/Aggregates/PaymentSaga.cs); [NetworkReconciliationController.cs](Controllers/NetworkReconciliationController.cs); `SagaBacklogHealthCheck` in [SagaStepRunner.cs](Infrastructure/Saga/SagaStepRunner.cs) |
| 20 | `GET /internal/v1/payments/by-idempotency-key/{key}` (a Digital Gold purchase's payment outcome) accepts ONLY the Partner Products Worker's machine token (`payments.read` + its client ID pinned); one payment by its random key, no lists or search; the answer carries the outcome, payee and network reference - not the paying account | A machine token would get the staff screens' reach; any client with the read scope could follow payments | Broken function-level / object-level authorization (OWASP API5, API1); excessive data exposure (API3) | [InternalPaymentStatusController.cs](Controllers/InternalPaymentStatusController.cs), policy `PaymentOutcomeForPartnerProducts` in [Program.cs](Program.cs) |
| 21 | Container image: built only by CI from a commit, chiseled non-root runtime (no shell, files read-only to the process), scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); no secret inside - the database connection string is no longer in `appsettings.json` and the Development files never enter the build | A developer-built or tampered image could run in an environment; a shell and package manager help an attacker who gets in; a password in a layer stays in the registry for good | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |