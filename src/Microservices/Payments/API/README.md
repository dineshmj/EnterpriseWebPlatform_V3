# Payments API

The Payments bounded context, and the home of the platform's **orchestrated saga**. A staff member captures a payment for a customer (assisted channel); the API records it and starts a `PaymentSaga`, which carries the payment out in the background:

```text
reserve funds (Accounts) → [approval by a payments officer, above the tier] → send to the payment network → settle funds (Accounts) → COMPLETED
```

If something fails after the funds were reserved, the saga **compensates**: it asks Accounts to release them. Business requirements: [Payments-Requirements.md](../doc/Payments-Requirements.md). The pattern and the comparison with onboarding's choreography: [Saga plan §2](../../../../doc/EWP-V3-Saga-Choreography-and-Orchestration-Plans.md#2-orchestration--payments).

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

## The saga

```text
RESERVE_FUNDS ─FundsReserved─► (AWAIT_APPROVAL ─approved─►) SEND_TO_NETWORK ─accepted─► SETTLE_FUNDS ─FundsSettled─► DONE   payment COMPLETED
RESERVE_FUNDS ─FundsReservationFailed─► DONE                                                                     payment REJECTED (nothing to undo)
SEND_TO_NETWORK ─refused / gave up─► RELEASE_FUNDS ─FundsReleased─► DONE                                         payment FAILED (funds released)
AWAIT_APPROVAL ─rejected by the officer─► RELEASE_FUNDS ─FundsReleased─► DONE                                     payment REJECTED (funds released)
RELEASE_FUNDS ─no reply after N tries─► STUCK                                                                    payment COMPENSATION_FAILED
```

| Situation | What the saga does |
|---|---|
| No reply from Accounts in time | Sends the command again (Accounts treats the same PaymentRef idempotently); timeout doubles 30 s → 5 min. |
| Reservation never confirmed (5 tries) | Releases, to be sure, and the payment ends FAILED. |
| Settlement not confirmed | Keeps trying: the money already left through the network, so settlement is never abandoned. |
| Release not confirmed (5 tries) | Stops (STUCK) and the payment is **COMPENSATION_FAILED**: it never claims an undo that did not happen. A late `FundsReleased` still resolves it; operations can also **Retry release** (a fresh `ReleaseFunds` with fresh attempts) from the payment page. |
| Network unavailable | Retries with back-off (5 s doubling, 4 attempts), then compensates. The PaymentRef is the network's Idempotency-Key, so a retry can never pay twice. |
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

ABAC: a staff member sees and starts payments for their own branch only; **operations** (`workflow.view`) and **auditors** (`payment.history.view`), whose work is not branch-bound, read every branch. Accounts additionally checks that the paying account belongs to the customer named in the payment.

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

Commands (to `accounts.commands`, key = PaymentRef): `ReserveFunds`, `SettleFunds`, `ReleaseFunds`. Events (to `payments.payment.events`): `PaymentCompleted`, `PaymentRejected`, `PaymentFailed`, `PaymentCompensationFailed`. All in the standard envelope (WorkflowId / CorrelationId per payment; CausationId = the message that led to it). Contracts: [Integration-Event-Catalogue.md](../../../../doc/Integration-Event-Catalogue.md).