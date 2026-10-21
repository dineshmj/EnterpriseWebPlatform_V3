# Circuit Breaker with Exponential Backoff - a conversation

Sophie sends AUD 140 from a customer's account to Harbour Plumbing. The payment has to go out over the payment network (NPP), and today the network is having a bad day. These are the parts of EWP that deal with it, talking like colleagues. Technical details are in *italics*.

| Who | In EWP |
|---|---|
| **Saga** | The Payments saga in the Payments API: it decides the payment's next step |
| **Retry** | The retry step in the Payments API's HTTP pipeline (Polly, `AddStandardResilienceHandler`) |
| **Breaker** | The circuit breaker in that same pipeline |
| **Stopwatch** | The per-attempt timeout (5 s) and the overall limit for one send, retries included (20 s) |
| **Network** | The payment network (in Development: the Payment Network simulator) |

There are two layers of "try again": a quick one inside a single send (Retry, within seconds) and a slow one across sends (the Saga, over minutes). The Breaker sits between them and the Network.

---

## A normal day

**Saga:** The funds are reserved, so it's time to send PAY-261015-UX9QAG. Same Idempotency-Key as always for this payment.

**Breaker:** I'm closed, all quiet. Go ahead.

**Network:** Received and settled. Here's my reference, NPP261015A3F….

**Breaker:** Noted, one success.

**Saga:** Great, I'll settle the funds. Payment completed.

---

## The network has a hiccup

**Saga:** Sending the next payment.

**Breaker:** Closed. Go ahead.

**Network:** *(503 Service Unavailable)*

**Retry:** That's a temporary kind of failure, so I'll try again. Don't worry, the Network recognises the Idempotency-Key, so a repeat can never pay twice. I'll wait about 2 seconds, a bit more or less at random so we don't all come back at the same moment, and then try.
*Exponential backoff with jitter, at most 2 retries.*

**Breaker:** I'm counting. That's one failure out of one call.

**Network:** OK, *now* it works. Settled.

**Breaker:** One failure out of two. That's fine, I stay closed.

**Saga:** Completed. The customer never noticed.

---

## The network is down

**Saga:** Sending PAY-261015-UX9QAG.

**Network:** *(503)*

**Retry:** Wait about 2 s, try again...

**Network:** *(503)*

**Retry:** ...about 4 s, last try...

**Network:** *(503)*

**Breaker:** That's enough. Three calls in the last 30 seconds and every one failed: at least half failing, with at least three calls, is my limit. I'm **opening**. For the next 30 seconds, nobody gets through to the Network. I'll refuse straight away instead of letting people wait for timeouts.
*FailureRatio 0.5, MinimumThroughput 3, SamplingDuration 30 s, BreakDuration 30 s.*

**Retry:** That was my last retry anyway. Saga, this send failed.

**Saga:** Understood. It's attempt 1 of 4. I'm not hammering anyone. I'll come back in 5 seconds, then 10, then 20, each wait twice the last, never more than 60.
*The Saga's own backoff: NetworkRetryInitialDelaySeconds 5, max 60, MaxNetworkAttempts 4. The wait is stored with the payment, so it survives a restart.*

**Saga** *(5 s later)***:** Attempt 2.

**Breaker:** Sorry, I'm open. No call.
*BrokenCircuitException: the Saga records "the circuit breaker is open".*

**Saga:** Fine, I'll wait 10 seconds.

**Breaker** *(30 s after opening)***:** My break is over. I'm **half-open**: I'll let *one* call through to see if the Network is back.

**Saga:** Attempt 3, then.

**Breaker:** You're my test call. Go.

**Network:** *(503)*

**Breaker:** Still down. I'm open again, for another 30 seconds.

**Saga:** Attempt 4 failed too, and that was my last send. But careful: I **don't know** whether any of those sends got through. A call that timed out might still have paid. So I won't release the customer's money on a guess. The funds stay reserved, and I'll *ask* the Network about this Idempotency-Key until it can answer.
*Step CONFIRM_WITH_NETWORK: the status enquiry is repeated with backoff and never gives up. Past the alert time (2 min in Development, 60 min in production), the operations team is alerted and can resolve the payment with four eyes.*

**Network** *(back up)***:** For that key? I never received it.

**Saga:** Now I know. Release the funds. Payment failed, and the money is back with the customer.

---

## The network is slow

**Saga:** Sending.

**Network:** *(thinking... 12 seconds...)*

**Stopwatch:** 5 seconds are up. This attempt is abandoned.

**Retry:** A timeout counts as temporary, so I'll try again with the *same* key.

**Network:** *(still thinking...)*

**Stopwatch:** Abandoned again. And the whole send has used up its 20 seconds.

**Breaker:** Timeouts count as failures for me too. That's enough failures: I'm opening.

**Network:** *(meanwhile, finishing the first request)* Payment settled, by the way. Nobody was listening anymore.

**Saga:** That send failed, so I'll try again later. *(Later, after the break:)* Sending again, same key.

**Network:** I've already got that one. Here's the same answer as before: settled, reference NPP….

**Saga:** Then it went out exactly once. Settle the funds. Completed.

---

## What doesn't count as a failure

**Network:** *(422)* The payee's bank is closed.

**Retry:** That's a real answer, not a hiccup. Asking again won't change it, so I won't retry.

**Breaker:** And it's not a sign that the Network is unwell, so it doesn't count towards opening me.
*Only connection errors, timeouts, 5xx, 408 and 429 count.*

**Saga:** A refusal is final. Release the funds. Payment failed, and the money is back with the customer.

---

## Who does what

| | Retry (Polly) | Breaker (Polly) | Saga |
|---|---|---|---|
| **Time scale** | seconds, within one send | 30-second windows | minutes, across sends |
| **Waits** | about 2 s, then 4 s (jittered) | 30 s break, then one test call | 5 s, 10 s, 20 s... up to 60 s |
| **Gives up** | after 2 retries or 20 s | never: opens and closes | after 4 sends, then *asks*, never guesses |
| **Lives** | in memory, per Payments API instance | in memory, per instance | in the database, with the payment |

- **Retry** smooths over blips that last a second or two.
- **Breaker** stops a whole instance from queueing up behind a dead dependency, and gives the Network room to recover.
- **Saga** owns the business outcome. It never pays twice (Idempotency-Key) and never releases money it isn't sure about (status enquiry).

*Code:*
- *`src/Microservices/Payments/API/Program.cs`*: the resilience pipeline.
- *`Domain/Aggregates/PaymentSaga.cs`*: the attempts, backoff and CONFIRM_WITH_NETWORK.
- *`Application/Commands/RunDueSagaStep.cs`*

*To see it live:* switch the Payment Network on the Operations → Simulator & Fault Controls page to Down, Slow or LosingAnswers, and watch the payment's Timeline.