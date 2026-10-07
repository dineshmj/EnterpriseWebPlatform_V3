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
    int MaxNetworkAttempts)
{
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

/// <summary>
/// Aggregate root and ORCHESTRATOR of one payment: the persisted state machine that decides
/// every next step. Participants (Accounts, the payment network) only do what they are
/// asked; nobody else decides what happens to the payment.
///
///   RESERVE_FUNDS ─FundsReserved─► (AWAIT_APPROVAL) ─► SEND_TO_NETWORK ─accepted─► SETTLE_FUNDS ─FundsSettled─► DONE (COMPLETED)
///   RESERVE_FUNDS ─FundsReservationFailed─► DONE (REJECTED, nothing to undo)
///   SEND_TO_NETWORK ─refused / gave up─► RELEASE_FUNDS ─FundsReleased─► DONE (FAILED)
///   RELEASE_FUNDS ─no reply after N tries─► STUCK (COMPENSATION_FAILED: operations retry)
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

        saga.Record(now, "STARTED", $"Payment {payment.PaymentNumber}: {PaymentRules.Format(payment.Amount)} {payment.Currency} from {payment.From} to {payment.To}." +
            (payment.ApprovalRequired ? " Above the approval tier: a payments officer must approve." : " Within the tier: no approval needed."), requestId);
        payment.StartReservingFunds(now);
        saga.SendFundsCommand(ReserveFundsCommand, payment, policy, now);
        return saga;
    }

    // ------------------------------------------------------------------ replies from Accounts

    /// <summary>Accounts reserved the funds.</summary>
    public void OnFundsReserved(Payment payment, Guid messageId, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReserveFunds, "FundsReserved", messageId, now))
            return;

        Record(now, "REPLY_RECEIVED", "FundsReserved: the funds are held for this payment.", messageId);
        payment.FundsReserved(now);
        LastMessageId = messageId;

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
    public void OnFundsReservationFailed(Payment payment, Guid messageId, string reasonCode, string reason, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReserveFunds, "FundsReservationFailed", messageId, now))
            return;

        Record(now, "REPLY_RECEIVED", $"FundsReservationFailed ({reasonCode}): {reason}", messageId);
        payment.RejectWithoutFunds(reasonCode, reason, now);
        LastMessageId = messageId;
        Finish(now, "Rejected: no funds were reserved, so there is nothing to undo.");
    }

    /// <summary>Accounts debited the held funds: the payment is complete.</summary>
    public void OnFundsSettled(Payment payment, Guid messageId, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.SettleFunds, "FundsSettled", messageId, now))
            return;

        Record(now, "REPLY_RECEIVED", "FundsSettled: the funds were debited.", messageId);
        payment.Complete(now);
        LastMessageId = messageId;
        Finish(now, "Completed.");
    }

    /// <summary>
    /// Accounts released the held funds: the compensation is complete. Also accepted when
    /// the saga had already given up (STUCK): the release did happen after all.
    /// </summary>
    public void OnFundsReleased(Payment payment, Guid messageId, bool nothingWasHeld, DateTimeOffset now)
    {
        if (!Expecting(SagaStep.ReleaseFunds, "FundsReleased", messageId, now, alsoWhenStuck: true))
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
    /// Idempotency-Key, so a retry can never pay twice); after the last attempt the funds
    /// are released. With a real network, a status enquiry would come before giving up.
    /// </summary>
    public void OnNetworkUnavailable(Payment payment, string error, SagaPolicy policy, DateTimeOffset now)
    {
        EnsureStep(SagaStep.SendToNetwork);
        Attempts++;
        LastError = Truncate(error);

        if (Attempts >= policy.MaxNetworkAttempts)
        {
            Record(now, "NETWORK_UNAVAILABLE", $"Attempt {Attempts} of {policy.MaxNetworkAttempts} failed: {LastError}. Giving up.", null);
            BeginCompensation(payment, PaymentStatus.Failed, "NETWORK_UNAVAILABLE",
                $"The payment network did not answer after {Attempts} attempts.", policy, now);
            return;
        }

        var delay = policy.NetworkRetryDelayFor(Attempts);
        NextCheckAt = now + delay;
        Touch(now);
        Record(now, "NETWORK_UNAVAILABLE", $"Attempt {Attempts} of {policy.MaxNetworkAttempts} failed: {LastError}. Next attempt in {delay.TotalSeconds:F0} s.", null);
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
            payment.From.Bsb, payment.From.AccountNumber, payment.Amount, payment.Currency, now));
        Record(now, "COMMAND_SENT", Attempts == 1 ? $"{commandType} sent to Accounts." : $"{commandType} sent to Accounts again (attempt {Attempts}).", messageId);
    }

    /// <summary>
    /// A reply counts only at the step that waits for it. A late or duplicated reply (after
    /// a resend, or redelivered by Kafka) changes nothing; it is recorded and ignored.
    /// </summary>
    private bool Expecting(SagaStep step, string replyType, Guid messageId, DateTimeOffset now, bool alsoWhenStuck = false)
    {
        if (Step == step && (Status == SagaStatus.Running || (alsoWhenStuck && Status == SagaStatus.Stuck)))
            return true;

        Record(now, "REPLY_IGNORED", $"{replyType} arrived at step {Step.ToCode()} ({Status.ToCode()}): late or duplicate, ignored.", messageId);
        return false;
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