using EnterpriseWebPlatform.Payments.Api.Domain.Common;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;

/// <summary>
/// The saga's tunable limits (configuration, Payments:Saga).
/// </summary>
public sealed record SagaPolicy(
    TimeSpan ReplyTimeout,
    TimeSpan MaxReplyTimeout,
    int MaxCommandAttempts,
    TimeSpan NetworkRetryInitialDelay,
    TimeSpan NetworkRetryMaxDelay,
    int MaxNetworkAttempts,
    TimeSpan UnconfirmedAlertAfter)
{
    public const int MinEvidenceLength = 10;
    public const int MaxEvidenceLength = 500;

    /// <summary>How long to wait for the reply to the n-th sending of a command (doubling, capped).</summary>
    public TimeSpan ReplyTimeoutFor(int attempt) => Doubling(ReplyTimeout, MaxReplyTimeout, attempt);

    public TimeSpan NetworkRetryDelayFor(int failedAttempts) => Doubling(NetworkRetryInitialDelay, NetworkRetryMaxDelay, failedAttempts);

    private static TimeSpan Doubling(TimeSpan initial, TimeSpan max, int n) =>
        TimeSpan.FromSeconds(Math.Min(max.TotalSeconds, initial.TotalSeconds * Math.Pow(2, Math.Clamp(n - 1, 0, 16))));
}

/// <summary>One line of the saga's history: what happened at which step (shown as the payment's timeline).</summary>
public sealed class SagaHistoryEntry
{
    private SagaHistoryEntry() { }

    public long Id { get; private set; }

    public Guid SagaId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public string Step { get; private set; } = null!;

    /// <summary>STARTED, COMMAND_SENT, REPLY_RECEIVED, REPLY_IGNORED, TIMEOUT, NETWORK_ACCEPTED, NETWORK_REFUSED, NETWORK_UNAVAILABLE, DECIDED, ...</summary>
    public string Kind { get; private set; } = null!;

    public string Detail { get; private set; } = null!;

    /// <summary>The message sent or received, if any (Kafka MessageId / Outbox ID).</summary>
    public Guid? MessageId { get; private set; }

    internal static SagaHistoryEntry Create(Guid sagaId, DateTimeOffset at, SagaStep step, string kind, string detail, Guid? messageId) =>
        new() { SagaId = sagaId, At = at, Step = step.ToCode(), Kind = kind, Detail = detail, MessageId = messageId };
}

// ┌─ :: ARCHITECTURE + RESILIENCE :: Orchestrated saga: a persisted state machine ─────────────────
// │ Why : A payment touches money in two places (the customer's account, the payment network) that
// │       must agree, without a distributed transaction; a crash, a lost answer or a late reply
// │       must never pay twice or lose money.
// │ How : The saga is an aggregate in the Payments database and the ONLY place decisions are made;
// │       each step is a short transaction (reply arrived, timer due, person decided) that saves
// │       the state and the next command (Outbox) together; compensation releases the funds; an
// │       unknown network outcome is reconciled, never released blind.
// │ Ref : Features 1.4.3 | Saga plan 2 | Saga pattern (orchestration) | Threat-Model R-1, R-5, R-12 |
// │       AI review 2: Microsoft EWP-04, EWP-06
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Aggregate root and ORCHESTRATOR of one payment: the persisted state machine that decides
/// every next step. Participants (Accounts, the payment network) only do what they are
/// asked; nobody else decides what happens to the payment.
///
///   RESERVE_FUNDS ─FundsReserved─► (AWAIT_APPROVAL) ─► SEND_TO_NETWORK ─accepted─► SETTLE_FUNDS ─FundsSettled─► DONE (COMPLETED)
///   RESERVE_FUNDS ─FundsReservationFailed─► DONE (REJECTED, nothing to undo)
///   SEND_TO_NETWORK ─refused─► RELEASE_FUNDS ─FundsReleased─► DONE (FAILED)
///   SEND_TO_NETWORK ─no answer after N tries─► CONFIRM_WITH_NETWORK (status enquiry, asked until it answers)
///       ─sent─► SETTLE_FUNDS (the money left: never released)   ─not sent / refused─► RELEASE_FUNDS (FAILED)
///   RELEASE_FUNDS ─no reply after N tries─► STUCK (COMPENSATION_FAILED: operations retry)
///   CONFIRM_WITH_NETWORK unanswered for long ─► alert; operations may PROPOSE sent / not sent with
///       evidence, a SECOND person approves (four eyes) ─► SETTLE_FUNDS or RELEASE_FUNDS. A time-out
///       alone never releases anything; the enquiry goes on, and the network's own answer wins.
///
/// Every call is a short, separate piece of work (a reply arrived, a timer is due); the
/// saga is saved after each one together with the payment and the next command, in one
/// transaction. Nothing waits in memory; a restart resumes from the saved step.
/// </summary>
public sealed class PaymentSaga : AggregateRoot
{
    public const string ReserveFundsCommand = "ReserveFunds";
    public const string SettleFundsCommand = "SettleFunds";
    public const string ReleaseFundsCommand = "ReleaseFunds";

    private readonly List<SagaHistoryEntry> _history = [];

    private PaymentSaga()
    {
        InitiatedByUserId = null!;
    }

    public Guid Id { get; private set; }

    public long PaymentId { get; private set; }

    public Guid PaymentRef { get; private set; }

    public SagaStep Step { get; private set; }

    public SagaStatus Status { get; private set; }

    /// <summary>Attempts at the current step (commands sent, or network calls).</summary>
    public int Attempts { get; private set; }

    /// <summary>When the step runner must look at the saga (a timeout or a due call); null when nothing is timed.</summary>
    public DateTimeOffset? NextCheckAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The MessageId of the command the current step waits on.</summary>
    public Guid? CurrentCommandId { get; private set; }

    /// <summary>The last message that moved the saga: the CausationId of what it sends next.</summary>
    public Guid? LastMessageId { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Guid CorrelationId { get; private set; }

    public string InitiatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Since when the saga has been asking the network about the payment (CONFIRM_WITH_NETWORK); null at any other step.</summary>
    public DateTimeOffset? UnconfirmedSince { get; private set; }

    /// <summary>When operations were alerted that the payment is unconfirmed for too long (once per wait); null before.</summary>
    public DateTimeOffset? UnconfirmedAlertedAt { get; private set; }

    // A manual resolution waiting for a second person's decision (null when none).
    public NetworkOutcome? ProposedOutcome { get; private set; }
    public string? ProposedNetworkReference { get; private set; }
    public string? ProposedEvidence { get; private set; }
    public string? ProposedByUserId { get; private set; }
    public string? ProposedByLabel { get; private set; }
    public DateTimeOffset? ProposedAt { get; private set; }

    public IReadOnlyList<SagaHistoryEntry> History => _history.AsReadOnly();

    // ------------------------------------------------------------------ start

    /// <summary>Starts the saga for a new payment: the first command is ReserveFunds.</summary>
    public static PaymentSaga Start(Payment payment, SagaPolicy policy, Guid requestId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.Id <= 0)
            throw new DomainConflictException("The payment must be saved before its saga starts.");

        var workflowId = Guid.NewGuid();
        var saga = new PaymentSaga
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            PaymentRef = payment.PaymentRef,
            Step = SagaStep.ReserveFunds,
            Status = SagaStatus.Running,
            WorkflowId = workflowId,
            CorrelationId = workflowId,
            LastMessageId = requestId,
            InitiatedByUserId = payment.InitiatedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        var fraud = payment.Fraud;
        saga.Record(now, "STARTED", $"Payment {payment.PaymentNumber}: {PaymentRules.Format(payment.Amount)} {payment.Currency} from {payment.From} to {payment.To}." +
            (fraud.Decision == FraudDecision.Decline ? string.Empty
                : fraud.Decision == FraudDecision.Review ? " The fraud check asks for a review: a payments officer must approve."
                : payment.ApprovalRequired ? " Above the approval tier: a payments officer must approve." : " Within the tier: no approval needed."), requestId);
        saga.Record(now, "FRAUD_CHECK", $"Fraud check by {fraud.Describe()}", requestId);

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ A declined payment still gets its saga - the timeline and the published PaymentRejected
        // │ come from it - but ends at once: no funds are reserved, nothing is sent.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (fraud.Decision == FraudDecision.Decline)
        {
            payment.DeclineForFraud(now);
            saga.Finish(now, "Rejected by the fraud check: nothing was reserved, so there is nothing to undo.");
            return saga;
        }

        payment.StartReservingFunds(now);
        saga.SendFundsCommand(ReserveFundsCommand, payment, policy, now);
        return saga;
    }

    // ------------------------------------------------------------------ replies from Accounts

    /// <summary>Accounts reserved the funds (in answer to the current ReserveFunds: causationId).</summary>
    public void OnFundsReserved(Payment payment, Guid messageId, Guid causationId, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReserveFunds, "FundsReserved", messageId, causationId, now))
            return;

        Record(now, "REPLY_RECEIVED", "FundsReserved: the funds are held for this payment.", messageId);
        payment.FundsReserved(now);
        LastMessageId = messageId;

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Above the approval tier, wait for a payments officer (no timer, a persisted state);
        // │ otherwise go straight to the payment network.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (payment.ApprovalRequired)
        {
            MoveTo(SagaStep.AwaitApproval, SagaStatus.WaitingForPerson, nextCheckAt: null, now);
            Record(now, "DECIDED", "Above the approval tier: waiting for a payments officer.", null);
        }
        else
        {
            MoveTo(SagaStep.SendToNetwork, SagaStatus.Running, nextCheckAt: now, now);
            Record(now, "DECIDED", "Within the tier: send to the payment network.", null);
        }
    }

    /// <summary>Accounts could not reserve the funds: the payment is rejected, nothing to undo.</summary>
    public void OnFundsReservationFailed(Payment payment, Guid messageId, Guid causationId, string reasonCode, string reason, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReserveFunds, "FundsReservationFailed", messageId, causationId, now))
            return;

        Record(now, "REPLY_RECEIVED", $"FundsReservationFailed ({reasonCode}): {reason}", messageId);
        payment.RejectWithoutFunds(reasonCode, reason, now);
        LastMessageId = messageId;
        Finish(now, "Rejected: no funds were reserved, so there is nothing to undo.");
    }

    /// <summary>Accounts debited the held funds: the payment is complete.</summary>
    public void OnFundsSettled(Payment payment, Guid messageId, Guid causationId, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.SettleFunds, "FundsSettled", messageId, causationId, now))
            return;

        Record(now, "REPLY_RECEIVED", "FundsSettled: the funds were debited.", messageId);
        payment.Complete(now);
        LastMessageId = messageId;
        Finish(now, "Completed.");
    }

    /// <summary>
    /// Accounts released the held funds: the compensation is complete. Also accepted when
    /// the saga had already given up (STUCK), and whichever ReleaseFunds attempt it answers:
    /// a confirmed release of this payment's hold is the fact the saga waits for.
    /// </summary>
    public void OnFundsReleased(Payment payment, Guid messageId, Guid causationId, bool nothingWasHeld, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReleaseFunds, "FundsReleased", messageId, causationId, now, alsoWhenStuck: true, anyAttempt: true))
            return;

        Record(now, "REPLY_RECEIVED", nothingWasHeld
            ? "FundsReleased: Accounts held nothing for this payment."
            : "FundsReleased: the reserved funds are available again.", messageId);
        payment.CompensationCompleted(now);
        LastMessageId = messageId;
        Finish(now, $"Ended {payment.Status.ToCode()} after compensation.");
    }

    // ------------------------------------------------------------------ the payments officer's decision (approval tier)

    /// <summary>
    /// A payments officer approved: the saga resumes (possibly days later) and sends the
    /// payment. The funds were reserved before the approval, so they are still there.
    /// </summary>
    public void OnApproved(Payment payment, string officerUserId, string officerLabel, int clearance, ApprovalLimits limits,
        string? remarks, Guid decisionId, DateTimeOffset now)
    {
        EnsureAwaitingApproval();
        payment.Approve(officerUserId, clearance, limits, remarks, now);
        LastMessageId = decisionId;
        Record(now, "APPROVED", $"Approved by {officerLabel}{(string.IsNullOrWhiteSpace(remarks) ? "." : $": {remarks.Trim()}")}", decisionId);
        MoveTo(SagaStep.SendToNetwork, SagaStatus.Running, nextCheckAt: now, now);
        Record(now, "DECIDED", "Approved: send to the payment network.", null);
    }

    /// <summary>A payments officer rejected: the reserved funds are released (compensation) and the payment ends REJECTED.</summary>
    public void OnApprovalRejected(Payment payment, string officerUserId, string officerLabel, string remarks,
        Guid decisionId, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureAwaitingApproval();
        payment.RecordRejection(officerUserId, remarks, now);
        LastMessageId = decisionId;
        Record(now, "REJECTED_BY_APPROVER", $"Rejected by {officerLabel}: {remarks.Trim()}", decisionId);
        BeginCompensation(payment, PaymentStatus.Rejected, "APPROVAL_REJECTED", $"Rejected by the payments officer: {remarks.Trim()}", policy, now);
    }

    private void EnsureAwaitingApproval()
    {
        if (Step != SagaStep.AwaitApproval || Status != SagaStatus.WaitingForPerson)
            throw new DomainConflictException("The payment is not waiting for an approval decision.");
    }

    // ------------------------------------------------------------------ the payment network (called by the step runner)

    public bool IsDueForNetwork(DateTimeOffset now) =>
        Status == SagaStatus.Running && Step == SagaStep.SendToNetwork && NextCheckAt <= now;

    /// <summary>The status enquiry is due (the network never answered the send).</summary>
    public bool IsDueForNetworkCheck(DateTimeOffset now) =>
        Status == SagaStatus.Running && Step == SagaStep.ConfirmWithNetwork && NextCheckAt <= now;

    /// <summary>
    /// Takes the network call (a send, or a status enquiry) before making it: the timer moves on by
    /// the lease, so no other step runner picks this saga while the call is on its way - and the
    /// call needs no open transaction or row lock. Should this process die mid-call, the lease runs
    /// out and the call is made again (the PaymentRef is the network's Idempotency-Key: never paid
    /// twice). Returns the claim: the saga's Version, which must be unchanged when the answer is recorded.
    /// </summary>
    public long ClaimNetworkAttempt(TimeSpan lease, DateTimeOffset now)
    {
        if (!IsDueForNetwork(now) && !IsDueForNetworkCheck(now))
            throw new DomainConflictException($"The saga is at {Step.ToCode()} ({Status.ToCode()}): no network attempt is due.");

        NextCheckAt = now + lease;
        Touch(now);
        return Version;
    }

    public void OnNetworkAccepted(Payment payment, string networkReference, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.SendToNetwork);
        Attempts++;
        Record(now, "NETWORK_ACCEPTED", $"The payment network accepted the payment (reference {networkReference}).", null);
        payment.SentToNetwork(networkReference, now);
        MoveTo(SagaStep.SettleFunds, SagaStatus.Running, null, now);
        SendFundsCommand(SettleFundsCommand, payment, policy, now);
    }

    /// <summary>The network said no (a permanent answer): release the reserved funds.</summary>
    public void OnNetworkRefused(Payment payment, string reason, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.SendToNetwork);
        Attempts++;
        Record(now, "NETWORK_REFUSED", $"The payment network refused the payment: {reason}", null);
        BeginCompensation(payment, PaymentStatus.Failed, "NETWORK_REFUSED", $"The payment network refused the payment: {reason}", policy, now);
    }

    /// <summary>
    /// The network did not answer. Retried with back-off (the PaymentRef is the network's
    /// Idempotency-Key, so a retry can never pay twice). After the last attempt the outcome is
    /// UNKNOWN - the payment may have gone out with only its answer lost - so nothing is released
    /// yet: the saga asks the network whether it sent the payment (CONFIRM_WITH_NETWORK).
    /// </summary>
    public void OnNetworkUnavailable(Payment payment, string error, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.SendToNetwork);
        Attempts++;
        LastError = Truncate(error);

        // ┌─ RESIL + SEC ────────────────────────────────────────────────────────────────────────
        // │ If the network never answered, the outcome is UNKNOWN (it may have sent the payment):
        // │ do not release - ask it (CONFIRM_WITH_NETWORK); otherwise retry with back-off (same
        // │ Idempotency-Key: never paid twice).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (Attempts >= policy.MaxNetworkAttempts)
        {
            Record(now, "NETWORK_UNAVAILABLE", $"Attempt {Attempts} of {policy.MaxNetworkAttempts} failed: {LastError}. No more sends.", null);
            MoveTo(SagaStep.ConfirmWithNetwork, SagaStatus.Running, nextCheckAt: now, now);
            Record(now, "DECIDED", "The outcome is unknown: ask the payment network whether it sent the payment before releasing anything.", null);
            return;
        }

        var delay = policy.NetworkRetryDelayFor(Attempts);
        NextCheckAt = now + delay;
        Touch(now);
        Record(now, "NETWORK_UNAVAILABLE", $"Attempt {Attempts} of {policy.MaxNetworkAttempts} failed: {LastError}. Next attempt in {delay.TotalSeconds:F0} s.", null);
    }

    // ------------------------------------------------------------------ reconciliation: the status enquiry (called by the step runner)

    /// <summary>The network confirms it SENT the payment - only the answer was lost: the money left, so settle, never release.</summary>
    public void OnNetworkConfirmedSent(Payment payment, string networkReference, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.ConfirmWithNetwork);
        SupersedeProposal(now);
        Record(now, "NETWORK_CONFIRMED_SENT", $"The payment network confirms it sent the payment (reference {networkReference}): settling the funds, not releasing them.", null);
        payment.SentToNetwork(networkReference, now);
        MoveTo(SagaStep.SettleFunds, SagaStatus.Running, null, now);
        SendFundsCommand(SettleFundsCommand, payment, policy, now);
    }

    /// <summary>The network confirms it did NOT send the payment (never received, or refused): releasing is now safe.</summary>
    public void OnNetworkConfirmedNotSent(Payment payment, string? refusalReason, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.ConfirmWithNetwork);
        SupersedeProposal(now);
        if (refusalReason is null)
        {
            Record(now, "NETWORK_CONFIRMED_NOT_SENT", "The payment network confirms it never received the payment: releasing the funds.", null);
            BeginCompensation(payment, PaymentStatus.Failed, "NETWORK_UNAVAILABLE",
                "The payment network did not answer, and confirms it never received the payment.", policy, now);
        }
        else
        {
            Record(now, "NETWORK_CONFIRMED_NOT_SENT", $"The payment network confirms it refused the payment: {refusalReason}", null);
            BeginCompensation(payment, PaymentStatus.Failed, "NETWORK_REFUSED", $"The payment network refused the payment: {refusalReason}", policy, now);
        }
    }

    /// <summary>
    /// The network cannot answer the enquiry either. The outcome stays unknown, so the funds stay
    /// reserved and the network is asked again, with back-off - for as long as it takes. The saga
    /// shows as overdue (health check, Processing Monitor) while this goes on.
    /// </summary>
    public void OnNetworkStatusUnknown(string error, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.ConfirmWithNetwork);
        Attempts++;
        LastError = Truncate(error);
        var delay = policy.NetworkRetryDelayFor(Attempts);
        NextCheckAt = now + delay;
        Touch(now);
        Record(now, "NETWORK_STATUS_UNKNOWN", $"Status enquiry {Attempts} failed: {LastError}. Asking again in {delay.TotalSeconds:F0} s; nothing is released until the network answers.", null);

        // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
        // │ Unconfirmed for too long: tell operations - once - that it needs reconciling.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (UnconfirmedAlertedAt is null && IsUnconfirmedTooLong(policy, now))
        {
            UnconfirmedAlertedAt = now;
            Record(now, "UNCONFIRMED_ALERT",
                $"Unconfirmed by the network for more than {policy.UnconfirmedAlertAfter.TotalMinutes:F0} minutes: operations are alerted and may now reconcile it (two people).", null);
            RaiseDomainEvent(new PaymentUnconfirmedByNetworkDomainEvent(UnconfirmedSince!.Value, now));
        }
    }

    // ------------------------------------------------------------------ reconciliation: by people, when the network cannot answer

    /// <summary>The network has not confirmed the payment for longer than the alert time (an operations alert; manual resolution possible).</summary>
    public bool IsUnconfirmedTooLong(SagaPolicy policy, DateTimeOffset now) =>
        Status == SagaStatus.Running && Step == SagaStep.ConfirmWithNetwork &&
        UnconfirmedSince is { } since && now - since >= policy.UnconfirmedAlertAfter;

    /// <summary>
    /// First pair of eyes: an operator who checked the network's own records (a statement, the
    /// network's operations desk) proposes what happened - SENT, with the network's reference for
    /// the payment, or NOT_SENT - with the evidence. Nothing changes yet: the enquiry goes on, and a
    /// second person must approve. Possible only once the payment is unconfirmed for the alert time.
    /// The saga's Version stays as it is, so an enquiry in flight still records its answer.
    /// </summary>
    public void ProposeNetworkResolution(NetworkOutcome outcome, string? networkReference, string evidence,
        string proposerUserId, string proposerLabel, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.ConfirmWithNetwork);
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A manual resolution is possible only after the alert time - before that the automatic
        // │ enquiry must have its chance.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!IsUnconfirmedTooLong(policy, now))
            throw new DomainConflictException(
                $"The payment network is still being asked. A manual resolution is possible once the payment has been unconfirmed for {policy.UnconfirmedAlertAfter.TotalMinutes:F0} minutes.");
        if (ProposedOutcome is not null)
            throw new DomainConflictException("A resolution is already waiting for a second person's decision.");
        if (proposerUserId == InitiatedByUserId)
            throw new SeparationOfDutiesViolationException("The person who initiated the payment cannot propose its resolution.");

        var reference = networkReference?.Trim();
        if (outcome == NetworkOutcome.Sent && (string.IsNullOrEmpty(reference) || reference.Length > 100))
            throw new DomainRuleViolationException("To resolve the payment as sent, give the network's reference for it (up to 100 characters).");
        var proof = RequiredText(evidence, "The evidence");

        ProposedOutcome = outcome;
        ProposedNetworkReference = outcome == NetworkOutcome.Sent ? reference : null;
        ProposedEvidence = proof;
        ProposedByUserId = proposerUserId;
        ProposedByLabel = proposerLabel;
        ProposedAt = now;
        UpdatedAt = now;

        Record(now, "RESOLUTION_PROPOSED", $"{proposerLabel} proposes: {Describe(outcome, ProposedNetworkReference)}. Evidence: {proof}. Waiting for a second person.", null);
        RaiseDomainEvent(new NetworkResolutionDomainEvent(NetworkResolutionDomainEvent.Proposed, outcome, ProposedNetworkReference, proof,
            proposerUserId, proposerUserId, null, now));
    }

    /// <summary>
    /// Second pair of eyes: another person (never the proposer, never the payment's initiator)
    /// approves - the payment is then settled (SENT) or its funds released (NOT_SENT) - or rejects
    /// the proposal with remarks, and the enquiry simply goes on.
    /// </summary>
    public void DecideNetworkResolution(Payment payment, bool approve, string deciderUserId, string deciderLabel, string? remarks,
        SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.ConfirmWithNetwork);
        if (ProposedOutcome is not { } outcome)
            throw new DomainConflictException("No resolution is waiting for a decision.");
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Second pair of eyes only: the proposer can never decide their own resolution, nor can
        // │ the payment's initiator.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (deciderUserId == ProposedByUserId)
            throw new SeparationOfDutiesViolationException("Four eyes: the person who proposed the resolution cannot decide it.");
        if (deciderUserId == InitiatedByUserId)
            throw new SeparationOfDutiesViolationException("The person who initiated the payment cannot decide its resolution.");

        var reference = ProposedNetworkReference;
        var evidence = ProposedEvidence!;
        var proposer = ProposedByUserId!;
        var note = string.IsNullOrWhiteSpace(remarks) ? null : RequiredText(remarks, "The remarks");

        if (!approve)
        {
            var why = RequiredText(remarks, "The reason for rejecting");
            Record(now, "RESOLUTION_REJECTED", $"{deciderLabel} rejected the proposed resolution ({Describe(outcome, reference)}): {why}. The enquiry goes on.", null);
            RaiseDomainEvent(new NetworkResolutionDomainEvent(NetworkResolutionDomainEvent.Rejected, outcome, reference, evidence, proposer, deciderUserId, why, now));
            ClearProposal();
            UpdatedAt = now;
            return;
        }

        Record(now, "RESOLUTION_APPROVED", $"{deciderLabel} approved the resolution proposed by {ProposedByLabel}: {Describe(outcome, reference)}." +
            (note is null ? string.Empty : $" Remarks: {note}"), null);
        RaiseDomainEvent(new NetworkResolutionDomainEvent(NetworkResolutionDomainEvent.Approved, outcome, reference, evidence, proposer, deciderUserId, note, now));
        ClearProposal();

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Approved as SENT: the money left - settle the reserved funds; approved as NOT SENT:
        // │ release them (FAILED).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (outcome == NetworkOutcome.Sent)
        {
            payment.SentToNetwork(reference!, now);
            MoveTo(SagaStep.SettleFunds, SagaStatus.Running, null, now);
            SendFundsCommand(SettleFundsCommand, payment, policy, now);
        }
        else
        {
            BeginCompensation(payment, PaymentStatus.Failed, "NETWORK_NOT_SENT_CONFIRMED",
                "The payment network never confirmed the payment; its records show it was not sent (confirmed by two people).", policy, now);
        }
    }

    private void SupersedeProposal(DateTimeOffset now)
    {
        if (ProposedOutcome is not { } outcome)
            return;
        Record(now, "RESOLUTION_SUPERSEDED", $"The network answered: the proposed resolution ({Describe(outcome, ProposedNetworkReference)}) by {ProposedByLabel} is no longer needed.", null);
        ClearProposal();
    }

    private void ClearProposal()
    {
        ProposedOutcome = null;
        ProposedNetworkReference = null;
        ProposedEvidence = null;
        ProposedByUserId = null;
        ProposedByLabel = null;
        ProposedAt = null;
    }

    private static string Describe(NetworkOutcome outcome, string? reference) =>
        outcome == NetworkOutcome.Sent ? $"the network SENT the payment (reference {reference})" : "the network did NOT send the payment";

    private static string RequiredText(string? text, string what)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length is < SagaPolicy.MinEvidenceLength or > SagaPolicy.MaxEvidenceLength)
            throw new DomainRuleViolationException($"{what} must be {SagaPolicy.MinEvidenceLength} to {SagaPolicy.MaxEvidenceLength} characters.");
        return value;
    }

    // ------------------------------------------------------------------ timeouts (called by the step runner)

    public bool IsWaitingForReply(DateTimeOffset now) =>
        Status == SagaStatus.Running && Step is SagaStep.ReserveFunds or SagaStep.SettleFunds or SagaStep.ReleaseFunds && NextCheckAt <= now;

    /// <summary>
    /// No reply from Accounts in time. The command is sent again (Accounts treats the same
    /// PaymentRef idempotently). What happens after the last attempt depends on the step:
    /// a reservation is abandoned and released to be sure; a settlement is never abandoned
    /// (the money already left through the network); a release that keeps failing stops
    /// the saga for operations - the saga never claims an undo that did not happen.
    /// </summary>
    public void OnReplyTimeout(Payment payment, SagaPolicy policy, DateTimeOffset now)
    {
        var command = CommandOf(Step);
        Record(now, "TIMEOUT", $"No reply to {command} (attempt {Attempts} of {policy.MaxCommandAttempts}).", CurrentCommandId);

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ No reply in time: resend the same command (Accounts is idempotent per PaymentRef). A
        // │ settlement is resent for ever - the money already left; a reservation is given up and
        // │ released; a release that keeps failing stops as COMPENSATION_FAILED for operations.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (Attempts < policy.MaxCommandAttempts || Step == SagaStep.SettleFunds)
        {
            SendFundsCommand(command, payment, policy, now);
            return;
        }

        if (Step == SagaStep.ReserveFunds)
        {
            BeginCompensation(payment, PaymentStatus.Failed, "ACCOUNTS_NOT_RESPONDING",
                "Accounts did not confirm the reservation in time.", policy, now);
            return;
        }

        // Release kept failing.
        LastError = $"No reply to ReleaseFunds after {Attempts} attempts.";
        payment.CompensationFailed(LastError, now);
        MoveTo(SagaStep.ReleaseFunds, SagaStatus.Stuck, null, now);
        Record(now, "COMPENSATION_FAILED", "Stopped: the release of the reserved funds is not confirmed. Operations must retry it.", null);
    }

    /// <summary>
    /// Operations retry the release of a stuck compensation (e.g. the release command was
    /// dead-lettered, or Accounts was down for long): a NEW ReleaseFunds is sent with fresh
    /// attempts. Accounts treats the same PaymentRef idempotently, so a release that did
    /// happen meanwhile is simply confirmed.
    /// </summary>
    public void RetryCompensation(Payment payment, string operatorLabel, SagaPolicy policy, DateTimeOffset now)
    {
        if (Status != SagaStatus.Stuck)
            throw new DomainConflictException("Only a stuck compensation can be retried.");

        payment.RetryCompensation(now);
        Attempts = 0;
        MoveTo(SagaStep.ReleaseFunds, SagaStatus.Running, null, now);
        LastError = null;
        Record(now, "RETRY", $"{operatorLabel} retried the release of the reserved funds.", null);
        SendFundsCommand(ReleaseFundsCommand, payment, policy, now);
    }

    /// <summary>
    /// Operations resent one of the payment's messages that the Outbox relay had PARKED (the
    /// broker could not take it). The saga's state does not change - the message simply goes
    /// out again, under its own id - but the payment's timeline says who did it and why. The
    /// saga's Version stays as it is, so a network call in flight still records its answer.
    /// </summary>
    public void RecordMessageResent(Guid messageId, string eventType, string operatorLabel, string reason, DateTimeOffset now) =>
        Record(now, "MESSAGE_RESENT", $"{operatorLabel} resent the parked {eventType} message: {reason}", messageId);

    // ------------------------------------------------------------------ internals

    private void BeginCompensation(Payment payment, PaymentStatus outcome, string code, string reason, SagaPolicy policy, DateTimeOffset now)
    {
        payment.BeginCompensation(outcome, code, reason, now);
        Attempts = 0;
        MoveTo(SagaStep.ReleaseFunds, SagaStatus.Running, null, now);
        Record(now, "DECIDED", $"Compensate: release the reserved funds ({code}).", null);
        SendFundsCommand(ReleaseFundsCommand, payment, policy, now);
    }

    /// <summary>Sends (or re-sends) a funds command and starts the reply timer.</summary>
    private void SendFundsCommand(string commandType, Payment payment, SagaPolicy policy, DateTimeOffset now)
    {
        Attempts++;
        var messageId = Guid.NewGuid();
        CurrentCommandId = messageId;
        NextCheckAt = now + policy.ReplyTimeoutFor(Attempts);
        Touch(now);

        RaiseDomainEvent(new FundsCommandIssuedDomainEvent(
            commandType, messageId, LastMessageId, PaymentRef, payment.PaymentNumber, payment.CustomerNumber,
            payment.From.Bsb, payment.From.AccountNumber, payment.Amount, payment.Currency, payment.BranchCode.Value, now));
        Record(now, "COMMAND_SENT", Attempts == 1 ? $"{commandType} sent to Accounts." : $"{commandType} sent to Accounts again (attempt {Attempts}).", messageId);
    }

    /// <summary>
    /// A reply counts only at the step that waits for it, and - for a reservation or a
    /// settlement - only as the answer to the command the saga sent LAST (its CausationId is
    /// that command's MessageId). A late or duplicated reply (after a resend, or redelivered
    /// by Kafka), or one answering an earlier attempt, changes nothing; it is recorded and
    /// ignored, and the answer to the current command follows (Accounts answers every command).
    /// </summary>
    private bool Expecting(SagaStep step, string replyType, Guid messageId, Guid causationId, DateTimeOffset now,
        bool alsoWhenStuck = false, bool anyAttempt = false)
    {
        if (Step != step || !(Status == SagaStatus.Running || (alsoWhenStuck && Status == SagaStatus.Stuck)))
        {
            Record(now, "REPLY_IGNORED", $"{replyType} arrived at step {Step.ToCode()} ({Status.ToCode()}): late or duplicate, ignored.", messageId);
            return false;
        }

        // ┌─ RESIL + SEC ────────────────────────────────────────────────────────────────────────
        // │ A reply counts only if it answers the command the saga sent LAST (CausationId); a
        // │ late answer to an earlier attempt is recorded and ignored - it can never move the
        // │ payment out of order.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!anyAttempt && causationId != CurrentCommandId)
        {
            Record(now, "REPLY_IGNORED", $"{replyType} answers an earlier command ({causationId}), not the current one: ignored.", messageId);
            return false;
        }

        return true;
    }

    private void EnsureStep(SagaStep step)
    {
        if (Step != step || Status != SagaStatus.Running)
            throw new DomainConflictException($"The saga is at {Step.ToCode()} ({Status.ToCode()}), not {step.ToCode()}.");
    }

    private void MoveTo(SagaStep step, SagaStatus status, DateTimeOffset? nextCheckAt, DateTimeOffset now)
    {
        if (step != Step)
        {
            Attempts = 0;   // attempts count per step
            LastError = null;
            UnconfirmedSince = step == SagaStep.ConfirmWithNetwork ? now : null;
            UnconfirmedAlertedAt = null;
            if (step != SagaStep.ConfirmWithNetwork)
                ClearProposal();
        }

        Step = step;
        Status = status;
        NextCheckAt = nextCheckAt;
        Touch(now);
    }

    private void Finish(DateTimeOffset now, string detail)
    {
        CurrentCommandId = null;
        MoveTo(SagaStep.Done, SagaStatus.Finished, null, now);
        Record(now, "FINISHED", detail, null);
    }

    private void Record(DateTimeOffset at, string kind, string detail, Guid? messageId) =>
        _history.Add(SagaHistoryEntry.Create(Id, at, Step, kind, detail, messageId));

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string CommandOf(SagaStep step) => step switch
    {
        SagaStep.ReserveFunds => ReserveFundsCommand,
        SagaStep.SettleFunds => SettleFundsCommand,
        SagaStep.ReleaseFunds => ReleaseFundsCommand,
        _ => throw new DomainConflictException($"Step {step.ToCode()} does not wait for Accounts.")
    };

    private static string Truncate(string text) => text.Length > 1000 ? text[..1000] : text;
}