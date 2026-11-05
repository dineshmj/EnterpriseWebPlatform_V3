using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

/// <summary>
/// How long the record must be kept before it may be erased, and how long a request waits for a
/// second person. Configuration (PersonalData:Erasure), passed in by the handlers.
/// </summary>
public sealed record PersonalDataErasurePolicy(TimeSpan RetentionPeriod, TimeSpan RequestValidFor);

// ┌─ :: SECURITY :: Erasing a person: four eyes, and never while the record must be kept ──────────
// │ Why : Erasure cannot be undone. One operator alone - mistaken, coerced or compromised - must not
// │       be able to wipe a customer, and the bank must not erase a record it still has to keep.
// │ How : One operator requests (with a reason code), ANOTHER approves or rejects; the requester
// │       can never decide their own request. Only a prospect untouched for the retention period
// │       qualifies - checked at the request AND again at the approval. One open request per
// │       customer; an undecided request expires. Approval destroys the customer's key in the same
// │       transaction (Customer.ErasePersonalData).
// │ Ref : doc/Crypto-Shredding.md | GDPR Art. 17 | APP 11.2 | AML/CTF Act s107 (retention) |
// │       OWASP A04 (business logic) | separation of duties
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Aggregate root: one request to erase one customer's personal data.
///
/// Lifecycle: PENDING → ERASED (a second person approved) | REJECTED (a second person refused)
/// | EXPIRED (nobody decided within <see cref="PersonalDataErasurePolicy.RequestValidFor"/>).
/// </summary>
public sealed class PersonalDataErasureRequest : AggregateRoot
{
    public const int MaxRemarksLength = 500;

    // For EF Core materialization.
    private PersonalDataErasureRequest()
    {
        CustomerNumber = null!;
        RequestedByUserId = null!;
    }

    private PersonalDataErasureRequest(
        long customerId,
        string customerNumber,
        PersonalDataErasureReason reason,
        string requestedByUserId,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        CustomerId = customerId;
        CustomerNumber = customerNumber;
        Reason = reason;
        Status = PersonalDataErasureStatus.Pending;
        RequestedByUserId = requestedByUserId;
        RequestedAt = now;
        ExpiresAt = expiresAt;
        Version = 1;
    }

    public long CustomerId { get; private set; }

    public string CustomerNumber { get; private set; }

    public PersonalDataErasureReason Reason { get; private set; }

    public PersonalDataErasureStatus Status { get; private set; }

    public string RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public string? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The decider's remarks (required to reject). Kept here only: never published.</summary>
    public string? DecisionRemarks { get; private set; }

    public long Version { get; private set; }

    /// <summary>PENDING and past its expiry: treated as EXPIRED (decided by nobody).</summary>
    public bool HasExpired(DateTimeOffset now) =>
        Status == PersonalDataErasureStatus.Pending && now >= ExpiresAt;

    /// <summary>First pair of eyes: an operator asks for the customer's personal data to be erased.</summary>
    public static PersonalDataErasureRequest Create(
        Customer customer,
        PersonalDataErasureReason reason,
        string requestedByUserId,
        PersonalDataErasurePolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(requestedByUserId))
            throw new DomainRuleViolationException("An erasure request needs the person who makes it.");

        if (!Enum.IsDefined(reason))
            throw new DomainRuleViolationException("Give the reason: the customer's request, or the end of the retention period.");

        // SEC: refused at once if the customer does not qualify - checked again at the approval.
        customer.EnsureErasable(policy, now);

        var request = new PersonalDataErasureRequest(
            customer.Id, customer.CustomerNumber.Value, reason, requestedByUserId.Trim(), now, now + policy.RequestValidFor);

        request.RaiseDomainEvent(new PersonalDataErasureRequestedDomainEvent(
            request.CustomerId, request.CustomerNumber, reason, request.RequestedByUserId, now));

        return request;
    }

    /// <summary>
    /// Second pair of eyes: another person approves - the customer's key is destroyed in the same
    /// unit of work - having checked again that the customer still qualifies.
    /// </summary>
    public void Approve(Customer customer, string decidedByUserId, PersonalDataErasurePolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        EnsureDecidable(decidedByUserId, now);

        if (customer.Id != CustomerId)
            throw new InvalidOperationException("The request names another customer.");

        customer.ErasePersonalData(policy, now);

        Decide(PersonalDataErasureStatus.Erased, decidedByUserId, null, now);
        RaiseDomainEvent(new CustomerPersonalDataErasedDomainEvent(
            Id, CustomerId, CustomerNumber, Reason, RequestedByUserId, DecidedByUserId!, now));
    }

    /// <summary>Second pair of eyes: another person refuses the erasure, saying why. Nothing is erased.</summary>
    public void Reject(string decidedByUserId, string? remarks, DateTimeOffset now)
    {
        EnsureDecidable(decidedByUserId, now);

        var why = remarks?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length < 10 || why.Length > MaxRemarksLength)
            throw new DomainRuleViolationException($"Say why the erasure is refused (10 to {MaxRemarksLength} characters).");

        Decide(PersonalDataErasureStatus.Rejected, decidedByUserId, why, now);
        RaiseDomainEvent(new PersonalDataErasureRejectedDomainEvent(
            Id, CustomerId, CustomerNumber, Reason, RequestedByUserId, DecidedByUserId!, now));
    }

    /// <summary>Closes a request nobody decided in time, so a new one can be made.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (!HasExpired(now))
            throw new DomainConflictException("Only an undecided request past its expiry can expire.");

        Status = PersonalDataErasureStatus.Expired;
        Version++;
    }

    private void EnsureDecidable(string decidedByUserId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(decidedByUserId))
            throw new DomainRuleViolationException("A decision needs the person who makes it.");

        if (Status != PersonalDataErasureStatus.Pending)
            throw new DomainConflictException($"The request was already decided ({Status.ToString().ToUpperInvariant()}).");

        if (HasExpired(now))
            throw new DomainConflictException("The request has expired: nobody decided it in time. Make a new request if the erasure is still needed.");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Four eyes: the requester can never decide their own request - not approve, and not
        // │ reject either (a rejection is a decision too). Compared by subject ID, the identity.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (string.Equals(decidedByUserId.Trim(), RequestedByUserId, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesViolationException("Four eyes: the person who requested the erasure cannot decide it. Another operator must approve or reject it.");
    }

    private void Decide(PersonalDataErasureStatus outcome, string decidedByUserId, string? remarks, DateTimeOffset now)
    {
        Status = outcome;
        DecidedByUserId = decidedByUserId.Trim();
        DecidedAt = now;
        DecisionRemarks = remarks;
        Version++;
    }
}