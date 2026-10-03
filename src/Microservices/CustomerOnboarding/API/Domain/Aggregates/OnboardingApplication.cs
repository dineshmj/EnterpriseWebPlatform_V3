using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

public sealed class OnboardingApplication : AggregateRoot
{
    private OnboardingApplication()
    {
        ApplicationNumber = null!; Customer = null!;
    }

    private OnboardingApplication(ApplicationNumber applicationNumber, long customerId)
    {
        ApplicationNumber = applicationNumber;
        CustomerId = customerId;
        Status = OnboardingApplicationStatus.Draft;
        CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = CreatedAt; Version = 1;
    }

    public ApplicationNumber ApplicationNumber { get; private set; }
    public long CustomerId { get; private set; }

    public Customer? Customer { get; private set; }
    // Customer is not part of the OnboardingApplication aggregate's domain state that needs to be constructed through the domain factory. CustomerId is the important domain relationship.
    // So constructor does not have a parameter to accept a Customer.

    public OnboardingApplicationStatus Status { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static OnboardingApplication Create(ApplicationNumber applicationNumber, long customerId)
    {
        ArgumentNullException.ThrowIfNull(applicationNumber);
        if (customerId <= 0) throw new DomainRuleViolationException("A valid customer is required.");
        return new OnboardingApplication(applicationNumber, customerId);
    }

    public void Submit()
    {
        EnsureStatus(OnboardingApplicationStatus.Draft);
        var previous = Status;
        Status = OnboardingApplicationStatus.Submitted;
        SubmittedAt = DateTimeOffset.UtcNow;
        Touch();
        RaiseDomainEvent(new OnboardingApplicationSubmittedDomainEvent(
            Id, CustomerId, ApplicationNumber.Value, UpdatedAt));
        RaiseDomainEvent(new OnboardingApplicationStatusChangedDomainEvent(
            Id, CustomerId, previous, Status, UpdatedAt));
    }

    public void StartKyc() => TransitionTo(OnboardingApplicationStatus.KycInProgress);
    public void CompleteKyc() => TransitionTo(OnboardingApplicationStatus.KycCompleted);
    public void StartCompliance() => TransitionTo(OnboardingApplicationStatus.ComplianceInProgress);
    public void CompleteCompliance() => TransitionTo(OnboardingApplicationStatus.ComplianceCompleted);
    public void StartAccountOpening() => TransitionTo(OnboardingApplicationStatus.AccountOpeningInProgress);

    public void Complete()
    {
        EnsureStatus(OnboardingApplicationStatus.AccountOpeningInProgress);
        var previous = Status;
        Status = OnboardingApplicationStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
        RaiseDomainEvent(new OnboardingApplicationStatusChangedDomainEvent(
            Id, CustomerId, previous, Status, UpdatedAt));
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
    public bool RecordKycCaseOpened()
    {
        if (Status != OnboardingApplicationStatus.Submitted)
            return false;

        StartKyc();
        return true;
    }

    /// <summary>KYC approved: (SUBMITTED →) KYC_IN_PROGRESS → KYC_COMPLETED.</summary>
    public bool RecordKycApproved()
    {
        var changed = RecordKycCaseOpened();

        if (Status != OnboardingApplicationStatus.KycInProgress)
            return changed;

        CompleteKyc();
        return true;
    }

    /// <summary>KYC rejected: SUBMITTED / KYC_IN_PROGRESS → REJECTED (terminal).</summary>
    public bool RecordKycRejected()
    {
        if (Status is not (OnboardingApplicationStatus.Submitted or OnboardingApplicationStatus.KycInProgress))
            return false;

        Reject();
        return true;
    }

    public void Reject() => SetTerminalStatus(OnboardingApplicationStatus.Rejected);
    public void Cancel() => SetTerminalStatus(OnboardingApplicationStatus.Cancelled);

    public void StartCompensation()
    {
        EnsureNotTerminal();
        SetStatus(OnboardingApplicationStatus.Compensating);
    }

    public void MarkCompensationFailed()
    {
        EnsureStatus(OnboardingApplicationStatus.Compensating);
        SetStatus(OnboardingApplicationStatus.CompensationFailed);
    }

    private void TransitionTo(OnboardingApplicationStatus target)
    {
        if (!IsValidTransition(Status, target))
            throw new DomainRuleViolationException($"Invalid onboarding application transition: {Status} -> {target}.");
        SetStatus(target);
    }

    private void SetTerminalStatus(OnboardingApplicationStatus target)
    {
        EnsureNotTerminal();
        SetStatus(target);
    }

    private void SetStatus(OnboardingApplicationStatus target)
    {
        var previous = Status;
        Status = target;
        Touch();
        RaiseDomainEvent(new OnboardingApplicationStatusChangedDomainEvent(
            Id, CustomerId, previous, Status, UpdatedAt));
    }

    private void EnsureStatus(OnboardingApplicationStatus expected)
    {
        if (Status != expected)
            throw new DomainRuleViolationException(
                $"Operation requires status '{expected}', but current status is '{Status}'.");
    }

    private void EnsureNotTerminal()
    {
        if (Status is OnboardingApplicationStatus.Completed or
            OnboardingApplicationStatus.Rejected or
            OnboardingApplicationStatus.Cancelled or
            OnboardingApplicationStatus.CompensationFailed)
            throw new DomainRuleViolationException($"Operation is not valid for terminal status '{Status}'.");
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

    private void Touch() { UpdatedAt = DateTimeOffset.UtcNow; Version++; }
}