using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

/// <summary>
/// Aggregate root: one onboarding application of one customer, and the saga state
/// of its cross-context workflow (KYC → Compliance → Account opening).
///
/// The customer is referenced by ID only (<see cref="CustomerId"/>): Customer is a
/// separate aggregate, and an application must never load or change it.
///
/// Invariants:
///  - Status moves only along the transition table below; terminal statuses are final.
///  - Only a draft can be submitted; only an application awaiting a decision can be rejected.
/// </summary>
public sealed class OnboardingApplication : AggregateRoot
{
    // For EF Core materialization.
    private OnboardingApplication()
    {
        ApplicationNumber = null!;
        BranchCode = null!;
    }

    private OnboardingApplication(
        ApplicationNumber applicationNumber,
        long customerId,
        BranchCode branchCode,
        DateTimeOffset now)
    {
        ApplicationRef = Guid.CreateVersion7(now);
        ApplicationNumber = applicationNumber;
        CustomerId = customerId;
        BranchCode = branchCode;
        Status = OnboardingApplicationStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    /// <summary>
    /// The application's identity for OTHER bounded contexts: a GUID that never
    /// repeats. <see cref="Entity.Id"/> is internal and restarts with the database.
    /// </summary>
    public Guid ApplicationRef { get; private set; }

    public ApplicationNumber ApplicationNumber { get; private set; }

    public long CustomerId { get; private set; }

    /// <summary>The branch the application was opened in (ABAC resource attribute).</summary>
    public BranchCode BranchCode { get; private set; }

    public OnboardingApplicationStatus Status { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsTerminal => Status is
        OnboardingApplicationStatus.Completed or
        OnboardingApplicationStatus.Rejected or
        OnboardingApplicationStatus.Cancelled or
        OnboardingApplicationStatus.CompensationFailed;

    public static OnboardingApplication Create(
        ApplicationNumber applicationNumber,
        long customerId,
        BranchCode branchCode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(applicationNumber);
        ArgumentNullException.ThrowIfNull(branchCode);
        if (customerId <= 0) throw new DomainRuleViolationException("A valid customer is required.");
        return new OnboardingApplication(applicationNumber, customerId, branchCode, now);
    }

    public void Submit(DateTimeOffset now)
    {
        EnsureStatus(OnboardingApplicationStatus.Draft);
        SubmittedAt = now;
        RaiseDomainEvent(new OnboardingApplicationSubmittedDomainEvent(
            Id, ApplicationRef, CustomerId, ApplicationNumber.Value, BranchCode.Value, now));
        SetStatus(OnboardingApplicationStatus.Submitted, now);
    }

    public void StartKyc(DateTimeOffset now) => TransitionTo(OnboardingApplicationStatus.KycInProgress, now);
    public void CompleteKyc(DateTimeOffset now) => TransitionTo(OnboardingApplicationStatus.KycCompleted, now);
    public void StartCompliance(DateTimeOffset now) => TransitionTo(OnboardingApplicationStatus.ComplianceInProgress, now);
    public void CompleteCompliance(DateTimeOffset now) => TransitionTo(OnboardingApplicationStatus.ComplianceCompleted, now);
    public void StartAccountOpening(DateTimeOffset now) => TransitionTo(OnboardingApplicationStatus.AccountOpeningInProgress, now);

    public void Complete(DateTimeOffset now)
    {
        EnsureStatus(OnboardingApplicationStatus.AccountOpeningInProgress);
        CompletedAt = now;
        SetStatus(OnboardingApplicationStatus.Completed, now);
    }

    // ------------------------------------------------------------------
    // Reactions to Customer KYC facts (saga choreography).
    //
    // KYC facts arrive asynchronously, at least once, and on different topics,
    // so they can be repeated or arrive out of order (e.g. "approved" before
    // "case opened"). Each method is therefore tolerant: it applies the
    // transition(s) still outstanding and is a no-op when the application is
    // already at or beyond the state the fact implies. Returns true when the
    // application changed.
    // ------------------------------------------------------------------

    /// <summary>KYC opened a case for this application: SUBMITTED → KYC_IN_PROGRESS.</summary>
    public bool RecordKycCaseOpened(DateTimeOffset now)
    {
        if (Status != OnboardingApplicationStatus.Submitted)
            return false;

        StartKyc(now);
        return true;
    }

    /// <summary>KYC approved: (SUBMITTED →) KYC_IN_PROGRESS → KYC_COMPLETED.</summary>
    public bool RecordKycApproved(DateTimeOffset now)
    {
        var changed = RecordKycCaseOpened(now);

        if (Status != OnboardingApplicationStatus.KycInProgress)
            return changed;

        CompleteKyc(now);
        return true;
    }

    /// <summary>KYC rejected: SUBMITTED / KYC_IN_PROGRESS → REJECTED (terminal).</summary>
    public bool RecordKycRejected(DateTimeOffset now)
    {
        if (Status is not (OnboardingApplicationStatus.Submitted or OnboardingApplicationStatus.KycInProgress))
            return false;

        Reject(now);
        return true;
    }

    /// <summary>
    /// A verifying context rejected the application. Only possible while a decision
    /// is pending (submitted, in KYC or in compliance) - never from a draft.
    /// </summary>
    public void Reject(DateTimeOffset now)
    {
        if (Status is not (OnboardingApplicationStatus.Submitted or
                           OnboardingApplicationStatus.KycInProgress or
                           OnboardingApplicationStatus.ComplianceInProgress))
        {
            throw new DomainRuleViolationException(
                $"An application can only be rejected while a decision is pending, not in status '{Status.ToCode()}'.");
        }

        SetStatus(OnboardingApplicationStatus.Rejected, now);
    }

    public void Cancel(DateTimeOffset now)
    {
        EnsureNotTerminal();
        SetStatus(OnboardingApplicationStatus.Cancelled, now);
    }

    public void StartCompensation(DateTimeOffset now)
    {
        EnsureNotTerminal();
        SetStatus(OnboardingApplicationStatus.Compensating, now);
    }

    public void MarkCompensationFailed(DateTimeOffset now)
    {
        EnsureStatus(OnboardingApplicationStatus.Compensating);
        SetStatus(OnboardingApplicationStatus.CompensationFailed, now);
    }

    private void TransitionTo(OnboardingApplicationStatus target, DateTimeOffset now)
    {
        if (!IsValidTransition(Status, target))
            throw new DomainRuleViolationException(
                $"Invalid onboarding application transition: {Status.ToCode()} -> {target.ToCode()}.");
        SetStatus(target, now);
    }

    private void SetStatus(OnboardingApplicationStatus target, DateTimeOffset now)
    {
        var previous = Status;
        Status = target;
        Touch(now);
        RaiseDomainEvent(new OnboardingApplicationStatusChangedDomainEvent(
            Id, ApplicationRef, CustomerId, previous, Status, now));
    }

    private void EnsureStatus(OnboardingApplicationStatus expected)
    {
        if (Status != expected)
            throw new DomainRuleViolationException(
                $"Operation requires status '{expected.ToCode()}', but current status is '{Status.ToCode()}'.");
    }

    private void EnsureNotTerminal()
    {
        if (IsTerminal)
            throw new DomainRuleViolationException($"Operation is not valid for terminal status '{Status.ToCode()}'.");
    }

    private static bool IsValidTransition(OnboardingApplicationStatus current, OnboardingApplicationStatus target) =>
        (current, target) switch
        {
            (OnboardingApplicationStatus.Submitted, OnboardingApplicationStatus.KycInProgress) => true,
            (OnboardingApplicationStatus.KycInProgress, OnboardingApplicationStatus.KycCompleted) => true,
            (OnboardingApplicationStatus.KycCompleted, OnboardingApplicationStatus.ComplianceInProgress) => true,
            (OnboardingApplicationStatus.ComplianceInProgress, OnboardingApplicationStatus.ComplianceCompleted) => true,
            (OnboardingApplicationStatus.ComplianceCompleted, OnboardingApplicationStatus.AccountOpeningInProgress) => true,
            _ => false
        };

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
