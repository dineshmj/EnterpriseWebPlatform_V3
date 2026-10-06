using EnterpriseWebPlatform.Payments.Api.Domain.Common;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: one payment instruction, captured by a staff member for a customer
/// (assisted channel) and carried out by the PaymentSaga. The payment owns the business
/// state people see; the saga owns the workflow (which step, which command, retries).
///
///   INITIATED → RESERVING_FUNDS → (PENDING_APPROVAL) → SENDING_TO_NETWORK → SETTLING_FUNDS → COMPLETED
///   RESERVING_FUNDS ✗ → REJECTED                         (nothing reserved, nothing to undo)
///   after the reservation ✗ → COMPENSATING → FAILED / REJECTED (funds released)
///   COMPENSATING ✗✗✗ → COMPENSATION_FAILED               (operations must act)
///
/// Invariants: the details never change after initiation; every transition starts from
/// the one state that allows it; an ended payment always says why.
/// </summary>
public sealed class Payment : AggregateRoot
{
    private Payment()
    {
        PaymentNumber = null!;
        CustomerNumber = null!;
        From = null!;
        PayeeName = null!;
        To = null!;
        Currency = null!;
        BranchCode = null!;
        InitiatedByUserId = null!;
    }

    public long Id { get; private set; }

    /// <summary>Never-repeating identity: the Idempotency-Key of the request that created it, and of every downstream call.</summary>
    public Guid PaymentRef { get; private set; }

    /// <summary>The business number people quote (e.g. PAY-261006-7K3QZ2).</summary>
    public string PaymentNumber { get; private set; }

    /// <summary>The paying customer (Customer Onboarding's customer, by value).</summary>
    public string CustomerNumber { get; private set; }

    public BankAccountRef From { get; private set; }

    /// <summary>The payee's account name as their bank holds it.</summary>
    public string PayeeName { get; private set; }

    public BankAccountRef To { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    /// <summary>What the payee sees on their statement.</summary>
    public string? Reference { get; private set; }

    /// <summary>ABAC: the branch of the staff member who captured it; only that branch sees it.</summary>
    public BranchCode BranchCode { get; private set; }

    public string InitiatedByUserId { get; private set; }

    /// <summary>Decided at initiation from the configured tier: above it, a payments officer approves.</summary>
    public bool ApprovalRequired { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>While COMPENSATING: the end state once the funds are released (FAILED or REJECTED).</summary>
    public PaymentStatus? CompensationOutcome { get; private set; }

    /// <summary>Why the payment did not complete (machine code and sentence).</summary>
    public string? OutcomeCode { get; private set; }

    public string? OutcomeReason { get; private set; }

    /// <summary>The payment network's reference once it accepted the payment.</summary>
    public string? NetworkReference { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public long Version { get; private set; }

    public bool HasEnded => Status is PaymentStatus.Completed or PaymentStatus.Rejected or PaymentStatus.Failed;

    public static Payment Initiate(
        Guid paymentRef,
        string paymentNumber,
        string customerNumber,
        BankAccountRef from,
        string payeeName,
        BankAccountRef to,
        decimal amount,
        string? reference,
        BranchCode branch,
        string initiatedByUserId,
        decimal approvalThreshold,
        DateTimeOffset now)
    {
        if (paymentRef == Guid.Empty)
            throw new DomainRuleViolationException("An Idempotency-Key (the payment's reference) is required.");
        if (string.IsNullOrWhiteSpace(initiatedByUserId))
            throw new DomainRuleViolationException("The initiating staff member is required.");
        if (from == to)
            throw new DomainRuleViolationException("The payee account must differ from the paying account.");

        var validAmount = PaymentRules.ValidAmount(amount);
        return new Payment
        {
            PaymentRef = paymentRef,
            PaymentNumber = paymentNumber,
            CustomerNumber = PaymentRules.ValidCustomerNumber(customerNumber),
            From = from,
            PayeeName = PaymentRules.ValidPayeeName(payeeName),
            To = to,
            Amount = validAmount,
            Currency = PaymentRules.Aud,
            Reference = PaymentRules.ValidReference(reference),
            BranchCode = branch,
            InitiatedByUserId = initiatedByUserId.Trim(),
            ApprovalRequired = validAmount > approvalThreshold,
            Status = PaymentStatus.Initiated,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;

    internal void StartReservingFunds(DateTimeOffset now) => Move(PaymentStatus.Initiated, PaymentStatus.ReservingFunds, now);

    internal void FundsReserved(DateTimeOffset now) =>
        Move(PaymentStatus.ReservingFunds, ApprovalRequired ? PaymentStatus.PendingApproval : PaymentStatus.SendingToNetwork, now);

    /// <summary>The funds could not be reserved: nothing to undo.</summary>
    internal void RejectWithoutFunds(string code, string reason, DateTimeOffset now)
    {
        Move(PaymentStatus.ReservingFunds, PaymentStatus.Rejected, now);
        End(code, reason, now);
        RaiseDomainEvent(new PaymentRejectedDomainEvent(code, reason, now));
    }

    internal void SentToNetwork(string networkReference, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(networkReference))
            throw new DomainRuleViolationException("The network's reference is required.");
        Move(PaymentStatus.SendingToNetwork, PaymentStatus.SettlingFunds, now);
        NetworkReference = networkReference.Trim();
    }

    internal void Complete(DateTimeOffset now)
    {
        Move(PaymentStatus.SettlingFunds, PaymentStatus.Completed, now);
        EndedAt = now;
        RaiseDomainEvent(new PaymentCompletedDomainEvent(NetworkReference!, now));
    }

    /// <summary>
    /// Something failed after the funds were reserved: they must be released. The payment
    /// ends as <paramref name="outcome"/> (FAILED or REJECTED) once Accounts confirms.
    /// </summary>
    internal void BeginCompensation(PaymentStatus outcome, string code, string reason, DateTimeOffset now)
    {
        if (outcome is not (PaymentStatus.Failed or PaymentStatus.Rejected))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A compensated payment ends FAILED or REJECTED.");
        if (Status is not (PaymentStatus.ReservingFunds or PaymentStatus.PendingApproval or PaymentStatus.SendingToNetwork))
            throw new DomainConflictException($"Payment {PaymentNumber} is {Status.ToCode()}: there is nothing to compensate.");

        Status = PaymentStatus.Compensating;
        CompensationOutcome = outcome;
        OutcomeCode = code;
        OutcomeReason = reason;
        Touch(now);
    }

    /// <summary>
    /// Accounts released the funds: the compensation is complete (also after it had been
    /// declared failed - a late confirmation is still the truth).
    /// </summary>
    internal void CompensationCompleted(DateTimeOffset now)
    {
        var outcome = CompensationOutcome ?? PaymentStatus.Failed;
        Move(Status == PaymentStatus.CompensationFailed ? PaymentStatus.CompensationFailed : PaymentStatus.Compensating, outcome, now);
        EndedAt = now;
        RaiseDomainEvent(outcome == PaymentStatus.Rejected
            ? new PaymentRejectedDomainEvent(OutcomeCode!, OutcomeReason!, now)
            : new PaymentFailedDomainEvent(OutcomeCode!, OutcomeReason!, now));
    }

    /// <summary>The release kept failing: the truth is recorded, never a rollback that did not happen.</summary>
    internal void CompensationFailed(string reason, DateTimeOffset now)
    {
        Move(PaymentStatus.Compensating, PaymentStatus.CompensationFailed, now);
        RaiseDomainEvent(new PaymentCompensationFailedDomainEvent(reason, now));
    }

    /// <summary>Operations retry the release of a payment whose compensation failed.</summary>
    internal void RetryCompensation(DateTimeOffset now) =>
        Move(PaymentStatus.CompensationFailed, PaymentStatus.Compensating, now);

    private void Move(PaymentStatus from, PaymentStatus to, DateTimeOffset now)
    {
        if (Status != from)
            throw new DomainConflictException($"Payment {PaymentNumber} is {Status.ToCode()}, not {from.ToCode()}: it cannot become {to.ToCode()}.");
        Status = to;
        Touch(now);
    }

    private void End(string code, string reason, DateTimeOffset now)
    {
        OutcomeCode = code;
        OutcomeReason = reason;
        EndedAt = now;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}