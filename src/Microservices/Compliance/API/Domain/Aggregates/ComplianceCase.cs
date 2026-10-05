using EnterpriseWebPlatform.Compliance.Api.Domain.Common;
using EnterpriseWebPlatform.Compliance.Api.Domain.Events;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: the compliance (financial-crime) review of ONE onboarding
/// application that KYC has approved.
///
/// Life cycle:
///   SCREENING ──screening result──► UNDER_REVIEW ──approve──► APPROVED
///                                       │  ▲      ──reject───► REJECTED
///                                  hold ▼  │ release
///                                     ON_HOLD ──reject──► REJECTED
///
/// Invariants enforced here (and backed by CHECK constraints in EwpComplianceDb.sql):
///  1. Screening must complete before review: a provider failure keeps the case in
///     SCREENING and is retried. A technical failure is never a "clear" result.
///  2. The screening verdict sets the risk, and the risk sets the clearance needed to
///     APPROVE (ABAC, see RiskPolicy). Rejecting or holding needs no extra clearance.
///  3. Separation of Duties (cross-context): the officer must be neither the workflow
///     initiator nor either KYC officer who decided the application. An unknown
///     initiator denies every action (fail closed).
///  4. ReBAC "assigned_to": only the assigned officer acts; the first action on an
///     unassigned case assigns it. The assignee may release it while it is open.
///  5. A rejection and a hold always carry a written reason.
///  6. Decision metadata exists exactly when the case is final.
///  7. The case screens the applicant as KYC verified them (a snapshot, never refreshed).
/// </summary>
public sealed class ComplianceCase : AggregateRoot
{
    private ComplianceCase()
    {
        ApplicationNumber = null!;
        CustomerNumber = null!;
        BranchCode = null!;
        Applicant = null!;
    }

    private ComplianceCase(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        long kycCaseId,
        BranchCode branchCode,
        Applicant applicant,
        string? initiatedByUserId,
        string? kycIdentityDecidedByUserId,
        string? kycDocumentDecidedByUserId,
        DateTimeOffset now)
    {
        ApplicationRef = applicationRef;
        Applicant = applicant;
        ApplicationNumber = applicationNumber;
        CustomerNumber = customerNumber;
        KycCaseId = kycCaseId;
        BranchCode = branchCode;
        InitiatedByUserId = initiatedByUserId;
        KycIdentityDecidedByUserId = kycIdentityDecidedByUserId;
        KycDocumentDecidedByUserId = kycDocumentDecidedByUserId;
        Status = ComplianceCaseStatus.Screening;
        NextScreeningAt = now;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public long Id { get; private set; }

    public Guid ApplicationRef { get; private set; }

    public string ApplicationNumber { get; private set; }

    public string CustomerNumber { get; private set; }

    public long KycCaseId { get; private set; }

    public BranchCode BranchCode { get; private set; }

    /// <summary>The applicant as KYC verified them: what is screened and what the officer sees.</summary>
    public Applicant Applicant { get; private set; }

    public string? InitiatedByUserId { get; private set; }

    public string? KycIdentityDecidedByUserId { get; private set; }

    public string? KycDocumentDecidedByUserId { get; private set; }

    public ComplianceCaseStatus Status { get; private set; }

    public ScreeningOutcome? ScreeningOutcome { get; private set; }

    public string? ScreeningProvider { get; private set; }

    public string? ScreeningReference { get; private set; }

    public DateTimeOffset? ScreenedAt { get; private set; }

    public int ScreeningAttempts { get; private set; }

    public DateTimeOffset? NextScreeningAt { get; private set; }

    public string? LastScreeningError { get; private set; }

    public RiskRating? RiskRating { get; private set; }

    public int? RequiredClearance { get; private set; }

    public string? AssignedOfficerUserId { get; private set; }

    public string? HoldReason { get; private set; }

    public string? DecisionByUserId { get; private set; }

    public DateTimeOffset? DecisionAt { get; private set; }

    public string? DecisionRemarks { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsTerminal => Status is ComplianceCaseStatus.Approved or ComplianceCaseStatus.Rejected;

    public static ComplianceCase Open(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        long kycCaseId,
        BranchCode branchCode,
        Applicant applicant,
        string? initiatedByUserId,
        string? kycIdentityDecidedByUserId,
        string? kycDocumentDecidedByUserId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(branchCode);
        ArgumentNullException.ThrowIfNull(applicant);

        if (applicationRef == Guid.Empty)
            throw new DomainRuleViolationException("A valid onboarding application reference is required.");
        if (string.IsNullOrWhiteSpace(applicationNumber))
            throw new DomainRuleViolationException("Application number is required.");
        if (string.IsNullOrWhiteSpace(customerNumber))
            throw new DomainRuleViolationException("Customer number is required.");
        if (kycCaseId <= 0)
            throw new DomainRuleViolationException("The approving KYC case must be identified.");

        var complianceCase = new ComplianceCase(
            applicationRef,
            applicationNumber.Trim(),
            customerNumber.Trim(),
            kycCaseId,
            branchCode,
            applicant,
            Normalize(initiatedByUserId),
            Normalize(kycIdentityDecidedByUserId),
            Normalize(kycDocumentDecidedByUserId),
            now);

        complianceCase.RaiseDomainEvent(new ComplianceCaseOpenedDomainEvent(now));
        return complianceCase;
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;

    public bool IsAssignedTo(string? officerUserId) =>
        AssignedOfficerUserId is not null &&
        string.Equals(AssignedOfficerUserId, officerUserId, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Screening

    /// <summary>The provider answered: record the verdict, rate the risk, move to review.</summary>
    public void RecordScreeningResult(
        ScreeningOutcome outcome,
        string provider,
        string reference,
        DateTimeOffset now)
    {
        if (Status != ComplianceCaseStatus.Screening)
            throw new DomainConflictException("The case is not waiting for screening.");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(reference))
            throw new DomainRuleViolationException("A screening result must name its provider and reference.");

        ScreeningOutcome = outcome;
        ScreeningProvider = provider.Trim();
        ScreeningReference = reference.Trim();
        ScreenedAt = now;
        ScreeningAttempts++;
        NextScreeningAt = null;
        LastScreeningError = null;

        RiskRating = RiskPolicy.RiskFor(outcome);
        RequiredClearance = RiskPolicy.RequiredClearanceToApprove(RiskRating.Value);
        Status = ComplianceCaseStatus.UnderReview;

        Touch(now);
        RaiseDomainEvent(new ScreeningCompletedDomainEvent(outcome, RiskRating.Value, now));
    }

    /// <summary>
    /// The provider could not be reached or failed. The case stays in SCREENING and
    /// is retried at <paramref name="nextAttemptAt"/>; it is never treated as clear.
    /// </summary>
    public void RecordScreeningFailure(string error, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        if (Status != ComplianceCaseStatus.Screening)
            throw new DomainConflictException("The case is not waiting for screening.");

        ScreeningAttempts++;
        LastScreeningError = string.IsNullOrWhiteSpace(error) ? "Screening failed." : error.Trim()[..Math.Min(error.Trim().Length, 2000)];
        NextScreeningAt = nextAttemptAt;
        Touch(now);
    }

    // ------------------------------------------------------------------ Assignment (ReBAC)

    public void Claim(string officerUserId, DateTimeOffset now)
    {
        EnsureOfficerMayAct(officerUserId);

        if (IsTerminal)
            throw new DomainConflictException("The compliance case is already decided.");
        if (IsAssignedTo(officerUserId))
            return;
        if (AssignedOfficerUserId is not null)
            throw new DomainConflictException("The compliance case is already assigned to another officer.");

        AssignTo(officerUserId, now);
        Touch(now);
    }

    public void Release(string officerUserId, DateTimeOffset now)
    {
        RequireOfficer(officerUserId);

        if (IsTerminal)
            throw new DomainConflictException("The compliance case is already decided.");
        if (!IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("Only the officer the case is assigned to can release it.");

        AssignedOfficerUserId = null;
        Touch(now);
        RaiseDomainEvent(new ComplianceCaseReleasedDomainEvent(officerUserId, now));
    }

    // ------------------------------------------------------------------ Decisions

    /// <summary>Approve: needs a completed screening and a clearance at least the risk requires.</summary>
    public void Approve(string officerUserId, int officerClearance, OfficerText? remarks, DateTimeOffset now)
    {
        TakeForAction(officerUserId, now, allowOnHold: false);

        if (RequiredClearance is not { } required)
            throw new DomainConflictException("The case has no risk rating yet.");
        if (officerClearance < required)
        {
            throw new ClearanceViolationException(
                $"Approving a {RiskRating!.Value.ToCode()}-risk case requires clearance level {required}; the officer has {officerClearance}.");
        }

        Decide(ComplianceCaseStatus.Approved, officerUserId, remarks, now);
    }

    /// <summary>Reject: from review or hold, always with remarks.</summary>
    public void Reject(string officerUserId, OfficerText? remarks, DateTimeOffset now)
    {
        if (remarks is null)
            throw new DomainRuleViolationException("Remarks are required to reject a compliance case.");

        TakeForAction(officerUserId, now, allowOnHold: true);
        Decide(ComplianceCaseStatus.Rejected, officerUserId, remarks, now);
    }

    public void PlaceOnHold(string officerUserId, OfficerText? reason, DateTimeOffset now)
    {
        if (reason is null)
            throw new DomainRuleViolationException("A reason is required to put a compliance case on hold.");

        TakeForAction(officerUserId, now, allowOnHold: false);

        Status = ComplianceCaseStatus.OnHold;
        HoldReason = reason.Value;
        Touch(now);
        RaiseDomainEvent(new ComplianceCaseHoldChangedDomainEvent(true, officerUserId, reason.Value, now));
    }

    public void ReleaseHold(string officerUserId, DateTimeOffset now)
    {
        EnsureOfficerMayAct(officerUserId);

        if (Status != ComplianceCaseStatus.OnHold)
            throw new DomainConflictException("The compliance case is not on hold.");
        if (!IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("Only the officer the case is assigned to can release the hold.");

        Status = ComplianceCaseStatus.UnderReview;
        HoldReason = null;
        Touch(now);
        RaiseDomainEvent(new ComplianceCaseHoldChangedDomainEvent(false, officerUserId, null, now));
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>SoD + state + ReBAC checks shared by every officer action; assigns an unassigned case.</summary>
    private void TakeForAction(string officerUserId, DateTimeOffset now, bool allowOnHold)
    {
        EnsureOfficerMayAct(officerUserId);

        if (IsTerminal)
            throw new DomainConflictException("The compliance case is already decided.");
        if (Status == ComplianceCaseStatus.Screening)
            throw new DomainConflictException("The case cannot be decided before its screening has completed.");
        if (Status == ComplianceCaseStatus.OnHold && !allowOnHold)
            throw new DomainConflictException("The case is on hold; release the hold first.");
        if (AssignedOfficerUserId is not null && !IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("The compliance case is assigned to another officer; only that officer can act on it.");

        if (AssignedOfficerUserId is null)
            AssignTo(officerUserId, now);
    }

    private void Decide(ComplianceCaseStatus outcome, string officerUserId, OfficerText? remarks, DateTimeOffset now)
    {
        var previous = Status;
        Status = outcome;
        HoldReason = null;
        DecisionByUserId = officerUserId;
        DecisionAt = now;
        DecisionRemarks = remarks?.Value;
        Touch(now);
        RaiseDomainEvent(new ComplianceCaseDecidedDomainEvent(previous, outcome, officerUserId, remarks?.Value, now));
    }

    private void EnsureOfficerMayAct(string officerUserId)
    {
        RequireOfficer(officerUserId);

        // Fail closed: without a known initiator the platform cannot prove that the
        // officer is not acting on their own workflow.
        if (InitiatedByUserId is null)
            throw new SeparationOfDutiesViolationException(
                "The workflow initiator of this application is unknown, so Separation of Duties cannot be verified. The action is denied.");

        if (Same(InitiatedByUserId, officerUserId))
            throw new SeparationOfDutiesViolationException("The workflow initiator cannot take or decide the compliance case.");

        if (Same(KycIdentityDecidedByUserId, officerUserId) || Same(KycDocumentDecidedByUserId, officerUserId))
            throw new SeparationOfDutiesViolationException(
                "An officer who decided this application's KYC cannot also take or decide its compliance case.");
    }

    private void AssignTo(string officerUserId, DateTimeOffset now)
    {
        AssignedOfficerUserId = officerUserId;
        RaiseDomainEvent(new ComplianceCaseAssignedDomainEvent(officerUserId, now));
    }

    private static void RequireOfficer(string officerUserId)
    {
        if (string.IsNullOrWhiteSpace(officerUserId))
            throw new DomainRuleViolationException("The officer must be identified.");
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}