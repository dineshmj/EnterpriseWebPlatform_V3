# Temporal and the Digital Gold purchase - a conversation

Camilla Parker signs a transfer authorization slip at EWP's counter: **AUD 20 of Digital Gold** from the **Partner Bank**. Sophie presses **Send & pay**. The order is placed and the payment starts in seconds, but the payment itself may take minutes (an approval above the tier, a slow payment network), and nobody waits at the counter for it. Something must follow the purchase to its end, survive restarts, and end it **exactly once**: gold allocated, or the unpaid order cancelled.

That something is a **Temporal workflow**. The parts involved talk to each other like colleagues. Technical details are in *italics*.

| Who | Where |
|---|---|
| **Sophie's screen** | Partner Products -> Digital Gold, in EWP's Shell |
| **Journey API** | The Partner Products Journey API (NestJS): composes the counter's steps, for Sophie |
| **Partner Products API** | EWP's record of the purchase, and EWP's adapter to the Partner Bank (mutual TLS, signed instructions) |
| **Payments API** | EWP Payments, with its own payment saga |
| **Temporal** | The Temporal server: keeps every workflow's history and timers (here the development server, `localhost:7233`) |
| **Worker** | The Partner Products Worker (Node.js): runs the workflow's code and its activities |
| **Workflow** | `goldPurchaseWorkflow` for this one purchase, ID `gold-purchase-DGP-261019-FZNEUT` |
| **Partner Bank** | Another bank, the system of record of the gold (reached only through the Partner Products API) |

---

## Before anything: two kinds of code

**Worker:** I run two kinds of code, and they follow different rules.

- **The workflow** decides what happens next. It must be **deterministic**: given the same history, it makes the same decisions. It never calls a server, reads the clock or picks a random number itself.
- **Activities** do the talking: an HTTP call to an API. Each one may fail, time out or be retried. Temporal records each activity's result in the workflow's history.

*The workflow code is bundled and runs in Temporal's sandbox, which refuses Node.js modules; activities run in plain Node.js. `src/workflows.ts` vs `src/activities.ts`.*

**Temporal:** And I write down everything that happens - workflow started, activity scheduled, activity completed with this result, timer started, timer fired - in an **event history** that outlives any worker.

---

## Part 1 - At the counter: Send & pay

**Sophie's screen:** Send & pay, purchase DGP-261019-FZNEUT.

**Journey API:** Three steps, all for Sophie, each safe to repeat.
1. Partner Products API, send the slip to the Partner Bank.
*A signed `digital_gold_purchase` instruction over mutual TLS. Answer: ORDERED, Partner Bank order PB-G-261019-ZWGE6B, the grams locked for 5 minutes.*
2. Partner Products API, what exactly should be paid? - Payments API, start this payment **as Sophie**.
*Payee: the Partner Bank's settlement account from configuration; amount and paying account from the slip; reference: the order number; `Idempotency-Key`: the purchase's payment key, fixed when the slip was recorded. Payments requires Sophie's own `payment.initiate`. Answer: PAY-261019-UXCDPL, and the Payments saga starts.*
3. Temporal, start a workflow.

**Journey API:** Temporal, start `goldPurchaseWorkflow` with ID `gold-purchase-DGP-261019-FZNEUT` on the task queue `partner-products`. The input is just `{ purchaseNumber: "DGP-261019-FZNEUT" }`.
*No token, no account number, no amount: the history is stored and shown in the Temporal UI. The worker reads what it needs through the APIs.*

**Temporal:** Started. Had a workflow with that ID already been running, I'd have said so instead of starting a second one.
*`WorkflowExecutionAlreadyStartedError` → "already running". One purchase, one workflow.*

**Sophie's screen:** "Order PB-G-261019-ZWGE6B placed at the Partner Bank; payment PAY-261019-UXCDPL started. EWP follows the payment now and tells the Partner Bank when it is made."

*Sophie can serve the next customer. Everything from here on runs without her.*

---

## Part 2 - The workflow waits, without holding anything

**Temporal:** Worker, there's a new task on `partner-products`.

**Worker:** Taking it. Workflow, you're on.

**Workflow:** First, where does the purchase stand?
*Activity `purchaseState` → the Partner Products API's internal endpoint, with the worker's own machine token.*

**Worker:** Partner Products API, purchase DGP-261019-FZNEUT, please.
*`GET /internal/v1/gold-purchases/DGP-261019-FZNEUT`. Token: client credentials of `PartnerProducts.Worker.M2M.ClientID`, scope `partner-products.write`. This endpoint pins that client ID and refuses any token with a person in it.*

**Partner Products API:** ORDERED, AUD 20.00, payment key `3f9c…`, order PB-G-261019-ZWGE6B.

**Temporal:** Noted in the history: `purchaseState` completed, with that result.

**Workflow:** Now, has the payment been made?
*Activity `paymentOutcome(paymentKey)`.*

**Worker:** Payments API, the payment with Idempotency-Key `3f9c…`?
*`GET /internal/v1/payments/by-idempotency-key/3f9c…`, a second machine token, scope `payments.read` (one token per API). Payments pins the worker's client ID here; it shows one payment by its random key - no lists, and not the paying account.*

**Payments API:** PAY-261019-UXCDPL is SENDING_TO_NETWORK.

**Workflow:** Not finished. I'll look again in 15 seconds.
*`sleep('15 seconds')` is a **durable timer**: Temporal keeps it, not the worker's memory.*

**Temporal:** Timer started. Worker, you may forget this workflow until it fires.

*Nothing is held while it waits: no thread, no database lock, no open connection. A thousand purchases waiting cost a thousand rows in Temporal, not a thousand sleeping processes.*

---

## Part 3 - The payment completes: gold allocated

**Temporal:** Timer fired. Worker, continue.

**Workflow:** Payment, again?

**Payments API:** PAY-261019-UXCDPL is **COMPLETED**, network reference NPP261019A1B2C3D4E5, AUD 20.00 to 182-512 961234567.

**Workflow:** Then send the payment advice.
*Activity `sendPaymentAdvice`.*

**Worker:** Partner Products API, payment advice for DGP-261019-FZNEUT: key `3f9c…`, reference NPP…, AUD 20.00, payee 182-512 961234567.

**Partner Products API:** Let me check this is really **this purchase's** payment: the key matches, the amount is the slip's, the payee is the settlement account. Yes. I'm saving **PAID** first - if anything goes wrong after this, nobody can forget that the money left Camilla's account. Now, Partner Bank...
*`MarkPaid`, saved; then `POST /api/gold-orders/PB-G-261019-ZWGE6B/payment-advice` over mutual TLS with EWP's bound token.*

**Partner Bank:** Payment of AUD 20.00 received from Four Walls. **0.1213 g** allocated to Camilla.

**Partner Products API:** **ALLOCATED**.

**Workflow:** Done. My result: `ALLOCATED`.

**Temporal:** Workflow completed. Its history stays for the retention period: anyone can see in the UI what happened, step by step - and nothing in it is a secret.

**Sophie's screen** (next look): Gold allocated, 0.1213 g. *The holding is read from the Partner Bank, never copied.*

---

## Part 4 - The worker dies in the middle

Rewind to Part 3. The advice has gone to the Partner Bank - and the worker's process is killed before it could tell Temporal.

**Temporal:** The activity `sendPaymentAdvice` started, but its worker went quiet. After its timeout I'll schedule it again.
*`startToCloseTimeout: 30 seconds`; the retry policy: 5 s, doubling, at most 5 minutes apart, no limit on attempts.*

**Worker** (a new process, started again): Hello, Temporal. Anything on `partner-products`?

**Temporal:** Yes: `gold-purchase-DGP-261019-FZNEUT`. Here is its history.

**Worker:** Workflow, you're on - from the beginning.

**Workflow:** Where does the purchase stand?

**Worker:** Temporal already has that answer in the history: ORDERED. I won't call the API again.
*This is **replay**: the workflow runs from the start, but every activity that already completed returns its recorded result. That's why the workflow code must be deterministic - it has to take the same path again.*

**Workflow:** Payment? *(Recorded: SENDING_TO_NETWORK.)* Sleep 15 s. *(Recorded: fired.)* Payment? *(Recorded: COMPLETED.)* Send the advice.

**Worker:** That one has no recorded result. Calling it - again.

**Partner Products API:** DGP-261019-FZNEUT is already **ALLOCATED**. Here is its state; I did nothing a second time.
*The endpoint is idempotent: ALLOCATED returns at once; PAID with the same network reference is not paid twice; and the Partner Bank allocates once per advice anyway.*

**Workflow:** Done: `ALLOCATED`. Exactly once, even though the advice was sent twice.

---

## Part 5 - The other endings

**The payment is not made.** Emily rejects it, or the network refuses it.

**Payments API:** PAY-… is **REJECTED** *(or FAILED, or COMPENSATION_FAILED - in each case no money reached the Partner Bank).*

**Workflow:** Then cancel the Partner Bank's unpaid order.
*Activity `cancelOrder`.*

**Partner Products API:** Only an **unpaid** order can be cancelled - this one is ORDERED, so: Partner Bank, cancel PB-G-…, "payment PAY-… was not made (REJECTED)".

**Partner Bank:** Cancelled.

**Partner Products API:** **CANCELLED**. A PAID purchase would never have got here.

---

**No payment was ever started.** Sophie's screen placed the order, but the payment call failed and nobody pressed "Continue".

**Payments API:** No payment with that key. *(404 → the activity returns `null`.)*

**Workflow:** I've been waiting without a payment for 30 minutes now. Cancel the unpaid order.

---

**The Partner Bank refuses the advice** (say, the order was already cancelled on its side).

**Partner Products API:** The money has left Camilla's account, so the purchase stays **PAID**; I note "the Partner Bank refused the payment advice: …" for a person, and answer 409.

**Worker:** A 409 can't be fixed by trying again: I fail this activity as **non-retryable**.
*400 / 403 / 409 / 404 → `ApplicationFailure.nonRetryable`.*

**Temporal:** Workflow failed - and its history shows exactly where and why. An operator can see it in the UI; Sophie sees the reason on the purchase.

---

**The Partner Bank or the IDP is down.**

**Partner Products API:** The Partner Bank could not be reached. The purchase stays PAID: 503.

**Worker:** A 503 is worth retrying. Temporal?

**Temporal:** In 5 seconds, then 10, 20 … at most every 5 minutes, for as long as it takes.
*Safe, because every step is idempotent.*

---

**Temporal is down when Sophie presses Send & pay.**

**Journey API:** I couldn't start the workflow. Nothing is lost: the order and the payment exist.

**Sophie's screen:** "The order is placed, but EWP could not start following the payment. Press **Continue** shortly." *Continue repeats only the open steps; the workflow starts once.*

---

## Part 6 - A payment that waits for days

**Workflow:** The payment is PENDING_APPROVAL, and Emily is on leave. I check every 15 seconds - that's 5,760 checks a day. My history would grow and grow.

**Temporal:** So after 240 checks (an hour), **continue as new**: close this run, start a fresh one with the same workflow ID and a clean history.

**Workflow:** And I carry over the one thing I need: how long I've waited without a payment.
*`continueAsNew({ purchaseNumber, waitedWithoutPaymentSeconds })`.*

---

## Why Temporal here, and a hand-built saga for Payments

| | Payments (the payment itself) | Digital Gold purchase (around it) |
|---|---|---|
| Coordinator | `PaymentSaga`, an aggregate in the Payments database | `goldPurchaseWorkflow`, in Temporal |
| State | rows in EWP's own database, with a timeline the screens show | Temporal's event history, plus the purchase's status in Partner Products |
| Talks to others through | Kafka commands and replies (Outbox, Inbox) | HTTP activities |
| Waits with | timers stored in the saga row, run by a step runner | Temporal's durable timers |
| Survives restarts by | re-reading the saga from its row | replaying the history |
| Fits because | many steps, money in two places, compensation, audit trail events | few steps, other organisations' timelines, "end it exactly once" |

**The two compose:** the purchase's payment **is** an ordinary Payments saga - approval tier, four eyes, funds hold and all. The workflow only watches its outcome and then deals with the Partner Bank.

- **Durable.** The worker can stop at any moment; Temporal resumes the workflow where it was.
- **Exactly one ending.** Allocate only on a completed payment, cancel only an unpaid order - checked again by the Partner Products API, and every call idempotent, so retries are always safe.
- **Nothing secret in the history.** The input is the purchase number; tokens are fetched by the worker for each call, with its own machine identity, and never recorded.
- **Its own, narrow identity.** The worker's token opens only two internal endpoints that pin its client ID - never a staff screen.

*Not yet (Threat-Model R-16): the worker polls the payment every 15 seconds instead of reacting to `payments.payment.events`; Temporal runs as the local development server (no TLS, no namespace authorization).*

*Code:*
- *EWP: `src/Microservices/PartnerProducts/Worker/src/workflows.ts` (the workflow), `activities.ts` (the calls and the retry rule), `machine-token.ts` (the worker's identity), `main.ts` (the worker and its health probe)*
- *EWP: `src/Microservices/PartnerProducts/JourneyApi/src/services/purchase-workflow.starter.ts` (start once), `src/digital-gold/digital-gold.service.ts` (`buy`: send, pay, start)*
- *EWP: `src/Microservices/PartnerProducts/API/Controllers/InternalGoldPurchasesController.cs` (payment advice, cancellation), `Domain/GoldPurchase.cs` (the states); `src/Microservices/Payments/API/Controllers/InternalPaymentStatusController.cs` (the payment's outcome)*
- *EWP: `ps/temporal/Start-Temporal.ps1` (the development server; UI http://localhost:8233)*
- *Rules and checks: `doc/Requirements/PartnerProducts-Requirements.md`, Features 1.4.4, Checklist 15*