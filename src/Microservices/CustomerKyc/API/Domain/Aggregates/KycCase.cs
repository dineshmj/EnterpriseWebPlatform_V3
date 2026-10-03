using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Common;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Events;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: the KYC verification of ONE onboarding application.
///
/// Invariants enforced here (and backed by CHECK constraints in EwpKycDb.sql):
///  1. Both stages start PENDING_REVIEW and are decided independently, in either order.
///  2. Only a pending stage can be decided; a terminal case cannot be decided at all.
///  3. Rejecting a stage requires remarks (at most 4000 characters).
///  4. The case status is derived: APPROVED when both stages are approved,
///     REJECTED as soon as one stage is rejected, otherwise PENDING_REVIEW.
///  5. Separation of Duties: the workflow initiator can never take or decide the
///     case, and when the initiator is unknown it is denied (fail closed).
///  6. ReBAC "assigned_to": only the assigned officer decides. An unassigned case
///     is assigned to the officer who takes it - by claiming it, or implicitly by
///     making the first decision. The assignee may release it before it is final.
///  7. The final decision metadata exists exactly when the case is terminal.
/// </summary>
public sealed class KycCase : AggregateRoot
{
    // For EF Core materialization.
    private KycCase()
    {
        ApplicationNumber = null!;
        CustomerNumber = null!;
        BranchCode = null!;
        IdentityVerification = null!;
        DocumentVerification = null!;
    }

    private KycCase(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        BranchCode branchCode,
        string? initiatedByUserId,
        DateTimeOffset now)
    {
        ApplicationRef = applicationRef;
        ApplicationNumber = applicationNumber;
        CustomerNumber = customerNumber;
        BranchCode = branchCode;
        InitiatedByUserId = initiatedByUserId;
        Status = KycCaseStatus.PendingReview;
        IdentityVerification = VerificationStage.Pending();
        DocumentVerification = VerificationStage.Pending();
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public long Id { get; private set; }

    /// <summary>The onboarding application this case verifies (Customer Onboarding's ApplicationRef).</summary>
    public Guid ApplicationRef { get; private set; }

    public string ApplicationNumber { get; private set; }

    public string CustomerNumber { get; private set; }

    /// <summary>ABAC: the branch the application was opened in.</summary>
    public BranchCode BranchCode { get; private set; }

    public KycCaseStatus Status { get; private set; }

    /// <summary>The human who started the onboarding workflow; never allowed to take or decide (SoD).</summary>
    public string? InitiatedByUserId { get; private set; }

    /// <summary>ReBAC: the officer the case is assigned to; null while in the shared queue.</summary>
    public string? AssignedOfficerUserId { get; private set; }

    public VerificationStage IdentityVerification { get; private set; }

    public VerificationStage DocumentVerification { get; private set; }

    /// <summary>Final decision metadata; set only when the case becomes APPROVED or REJECTED.</summary>
    public string? DecisionByUserId { get; private set; }

    public DateTimeOffset? DecisionAt { get; private set; }

    public string? DecisionRemarks { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token; incremented by every change.</summary>
    public long Version { get; private set; }

    public bool IsTerminal => Status is KycCaseStatus.Approved or KycCaseStatus.Rejected;

    public static KycCase Open(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        BranchCode branchCode,
        string? initiatedByUserId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(branchCode);

        if (applicationRef == Guid.Empty)
            throw new DomainRuleViolationException("A valid onboarding application reference is required.");

        if (string.IsNullOrWhiteSpace(applicationNumber))
            throw new DomainRuleViolationException("Application number is required.");

        if (string.IsNullOrWhiteSpace(customerNumber))
            throw new DomainRuleViolationException("Customer number is required.");

        var kycCase = new KycCase(
            applicationRef,
            applicationNumber.Trim(),
            customerNumber.Trim(),
            branchCode,
            string.IsNullOrWhiteSpace(initiatedByUserId) ? null : initiatedByUserId.Trim(),
            now);

        kycCase.RaiseDomainEvent(new KycCaseOpenedDomainEvent(now));
        return kycCase;
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;

    public bool IsAssignedTo(string? officerUserId) =>
        AssignedOfficerUserId is not null &&
        string.Equals(AssignedOfficerUserId, officerUserId, StringComparison.OrdinalIgnoreCase);

    public VerificationStage StageOf(VerificationStageType stage) =>
        stage == VerificationStageType.IdentityVerification ? IdentityVerification : DocumentVerification;

    /// <summary>An officer takes the case from the shared work queue.</summary>
    public void Claim(string officerUserId, DateTimeOffset now)
    {
        RequireOfficer(officerUserId);
        EnsureSeparationOfDuties(officerUserId);

        if (IsTerminal)
            throw new DomainConflictException("The KYC case has already reached a terminal state.");

        if (IsAssignedTo(officerUserId))
            return;

        if (AssignedOfficerUserId is not null)
            throw new DomainConflictException("The KYC case is already assigned to another officer.");

        AssignTo(officerUserId, now);
        Touch(now);
    }

    /// <summary>The assigned officer returns the case to the shared work queue.</summary>
    public void Release(string officerUserId, DateTimeOffset now)
    {
        RequireOfficer(officerUserId);

        if (IsTerminal)
            throw new DomainConflictException("The KYC case has already reached a terminal state.");

        if (!IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("Only the officer the case is assigned to can release it.");

        AssignedOfficerUserId = null;
        Touch(now);
        RaiseDomainEvent(new KycCaseReleasedDomainEvent(officerUserId, now));
    }

    /// <summary>
    /// The assigned officer approves or rejects one stage. Raises VerificationStageDecided,
    /// and KycCaseDecided when the decision makes the case terminal.
    /// </summary>
    public void DecideStage(
        VerificationStageType stage,
        StageDecision decision,
        string decidedByUserId,
        DecisionRemarks? remarks,
        DateTimeOffset now)
    {
        RequireOfficer(decidedByUserId);
        EnsureSeparationOfDuties(decidedByUserId);

        if (IsTerminal)
            throw new DomainConflictException("The KYC case has already reached a terminal state.");

        if (AssignedOfficerUserId is not null && !IsAssignedTo(decidedByUserId))
            throw new AssignmentViolationException("The KYC case is assigned to another officer; only that officer can decide it.");

        var previousStage = StageOf(stage);
        var decidedStage = previousStage.Decide(stage, decision, decidedByUserId, now, remarks);

        // The first decision on an unassigned case assigns it to the deciding officer.
        if (AssignedOfficerUserId is null)
            AssignTo(decidedByUserId, now);

        if (stage == VerificationStageType.IdentityVerification)
            IdentityVerification = decidedStage;
        else
            DocumentVerification = decidedStage;

        var previousStatus = Status;
        Status = DeriveStatus();

        if (IsTerminal)
        {
            DecisionByUserId = decidedByUserId;
            DecisionAt = now;
            DecisionRemarks = remarks?.Value;
        }

        Touch(now);

        RaiseDomainEvent(new VerificationStageDecidedDomainEvent(
            stage,
            previousStage.Status,
            decidedStage.Status,
            decidedByUserId,
            remarks?.Value,
            Status,
            now));

        if (IsTerminal)
        {
            RaiseDomainEvent(new KycCaseDecidedDomainEvent(
                previousStatus,
                Status,
                decidedByUserId,
                remarks?.Value,
                now));
        }
    }

    // The caller touches the aggregate once per command.
    private void AssignTo(string officerUserId, DateTimeOffset now)
    {
        AssignedOfficerUserId = officerUserId;
        RaiseDomainEvent(new KycCaseAssignedDomainEvent(officerUserId, now));
    }

    private static void RequireOfficer(string officerUserId)
    {
        if (string.IsNullOrWhiteSpace(officerUserId))
            throw new DomainRuleViolationException("The officer must be identified.");
    }

    private void EnsureSeparationOfDuties(string officerUserId)
    {
        // Fail closed: without a known initiator the platform cannot prove that the
        // officer is not acting on their own workflow, so the action is denied.
        if (InitiatedByUserId is null)
        {
            throw new SeparationOfDutiesViolationException(
                "The workflow initiator of this KYC case is unknown, so Separation of Duties cannot be verified. The action is denied.");
        }

        if (string.Equals(InitiatedByUserId, officerUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SeparationOfDutiesViolationException(
                "The workflow initiator cannot take, approve or reject this KYC case.");
        }
    }

    private KycCaseStatus DeriveStatus()
    {
        if (IdentityVerification.Status == VerificationStatus.Rejected ||
            DocumentVerification.Status == VerificationStatus.Rejected)
            return KycCaseStatus.Rejected;

        return IdentityVerification.IsApproved && DocumentVerification.IsApproved
            ? KycCaseStatus.Approved
            : KycCaseStatus.PendingReview;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}