using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: opening the account for ONE onboarding application that Compliance
/// approved - the last step of the onboarding saga.
///
/// Life cycle:
///   PENDING_REVIEW ──approve──► OPENING ──core banking opened──► OPENED
///     │  ▲        ──reject───► REJECTED   │
///  hold ▼  │ release                       └─refused / kept failing──► FAILED
///   ON_HOLD ──reject──► REJECTED
///
/// (APPROVED is a moment, not a resting state: approval records the decision and hands
/// the opening to the core-banking system at once.)
///
/// Invariants enforced here (and backed by CHECK constraints in EwpAccountsDb.sql):
///  1. Separation of Duties (cross-context): the account officer must be neither the
///     workflow initiator nor the Compliance officer who approved the application.
///     An unknown initiator denies every action (fail closed).
///  2. ReBAC "assigned_to": only the assigned officer acts; the first action on an
///     unassigned application assigns it. The assignee may release it while undecided.
///  3. A rejection and a hold always carry a written reason.
///  4. An account is opened only by the core-banking system after approval. A technical
///     failure keeps the application OPENING and is retried; it never counts as opened.
///     Too many failures, or a refusal, end it FAILED - the trigger for compensation.
/// </summary>
public sealed class AccountApplication : AggregateRoot
{
    private AccountApplication()
    {
        ApplicationNumber = null!;
        CustomerNumber = null!;
        BranchCode = null!;
        HolderName = null!;
    }

    private AccountApplication(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        long complianceCaseId,
        BranchCode branchCode,
        HolderName holderName,
        string? initiatedByUserId,
        string? complianceApprovedByUserId,
        DateTimeOffset now)
    {
        ApplicationRef = applicationRef;
        HolderName = holderName;
        ApplicationNumber = applicationNumber;
        CustomerNumber = customerNumber;
        ComplianceCaseId = complianceCaseId;
        BranchCode = branchCode;
        InitiatedByUserId = initiatedByUserId;
        ComplianceApprovedByUserId = complianceApprovedByUserId;
        Status = AccountApplicationStatus.PendingReview;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public long Id { get; private set; }

    public Guid ApplicationRef { get; private set; }

    public string ApplicationNumber { get; private set; }

    public string CustomerNumber { get; private set; }

    /// <summary>The name the account is opened in (as Compliance cleared it).</summary>
    public HolderName HolderName { get; private set; }

    /// <summary>The Compliance case that approved the application (by value).</summary>
    public long ComplianceCaseId { get; private set; }

    public BranchCode BranchCode { get; private set; }

    public string? InitiatedByUserId { get; private set; }

    public string? ComplianceApprovedByUserId { get; private set; }

    public AccountApplicationStatus Status { get; private set; }

    public AccountProduct? Product { get; private set; }

    public string? AssignedOfficerUserId { get; private set; }

    public string? HoldReason { get; private set; }

    public string? DecisionByUserId { get; private set; }

    public DateTimeOffset? DecisionAt { get; private set; }

    public string? DecisionRemarks { get; private set; }

    /// <summary>
    /// The officer's decision command (its ID is the CausationId of what it led to). The
    /// account is opened later by a background worker; its events still name this decision
    /// as their cause, so the causation chain stays unbroken.
    /// </summary>
    public Guid? DecisionId { get; private set; }

    public int OpeningAttempts { get; private set; }

    public DateTimeOffset? NextOpeningAt { get; private set; }

    public string? LastOpeningError { get; private set; }

    public string? AccountNumber { get; private set; }

    public DateTimeOffset? OpenedAt { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>The officer's decision is final (rejected, or approved and handed to core banking).</summary>
    public bool IsDecided => Status is AccountApplicationStatus.Rejected or AccountApplicationStatus.Opening
        or AccountApplicationStatus.Opened or AccountApplicationStatus.Failed;

    public static AccountApplication Open(
        Guid applicationRef,
        string applicationNumber,
        string customerNumber,
        long complianceCaseId,
        BranchCode branchCode,
        HolderName holderName,
        string? initiatedByUserId,
        string? complianceApprovedByUserId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(branchCode);
        ArgumentNullException.ThrowIfNull(holderName);

        if (applicationRef == Guid.Empty)
            throw new DomainRuleViolationException("A valid onboarding application reference is required.");
        if (string.IsNullOrWhiteSpace(applicationNumber))
            throw new DomainRuleViolationException("Application number is required.");
        if (string.IsNullOrWhiteSpace(customerNumber))
            throw new DomainRuleViolationException("Customer number is required.");
        if (complianceCaseId <= 0)
            throw new DomainRuleViolationException("The approving compliance case must be identified.");

        var application = new AccountApplication(
            applicationRef,
            applicationNumber.Trim(),
            customerNumber.Trim(),
            complianceCaseId,
            branchCode,
            holderName,
            Normalize(initiatedByUserId),
            Normalize(complianceApprovedByUserId),
            now);

        application.RaiseDomainEvent(new AccountApplicationCreatedDomainEvent(now));
        return application;
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;

    public bool IsAssignedTo(string? officerUserId) =>
        AssignedOfficerUserId is not null &&
        string.Equals(AssignedOfficerUserId, officerUserId, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Assignment (ReBAC)

    public void Claim(string officerUserId, DateTimeOffset now)
    {
        EnsureOfficerMayAct(officerUserId);

        if (IsDecided)
            throw new DomainConflictException("The account application is already decided.");
        if (IsAssignedTo(officerUserId))
            return;
        if (AssignedOfficerUserId is not null)
            throw new DomainConflictException("The account application is already assigned to another officer.");

        AssignTo(officerUserId, now);
        Touch(now);
    }

    public void Release(string officerUserId, DateTimeOffset now)
    {
        RequireOfficer(officerUserId);

        if (IsDecided)
            throw new DomainConflictException("The account application is already decided.");
        if (!IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("Only the officer the application is assigned to can release it.");

        AssignedOfficerUserId = null;
        Touch(now);
        RaiseDomainEvent(new AccountApplicationReleasedDomainEvent(officerUserId, now));
    }

    // ------------------------------------------------------------------ Decisions

    /// <summary>
    /// Approve: records the decision and the product, and hands the opening to the
    /// core-banking system (OPENING, due now).
    /// </summary>
    public void Approve(string officerUserId, AccountProduct product, OfficerText? remarks, Guid decisionId, DateTimeOffset now)
    {
        TakeForAction(officerUserId, now, allowOnHold: false);

        Product = product;
        DecisionId = decisionId;
        HoldReason = null;
        DecisionByUserId = officerUserId;
        DecisionAt = now;
        DecisionRemarks = remarks?.Value;
        Status = AccountApplicationStatus.Opening;
        NextOpeningAt = now;
        Touch(now);
        RaiseDomainEvent(new AccountApplicationApprovedDomainEvent(officerUserId, product, now));
    }

    /// <summary>Reject: from review or hold, always with remarks.</summary>
    public void Reject(string officerUserId, OfficerText? remarks, Guid decisionId, DateTimeOffset now)
    {
        if (remarks is null)
            throw new DomainRuleViolationException("Remarks are required to reject an account application.");

        TakeForAction(officerUserId, now, allowOnHold: true);

        var previous = Status;
        Status = AccountApplicationStatus.Rejected;
        DecisionId = decisionId;
        HoldReason = null;
        DecisionByUserId = officerUserId;
        DecisionAt = now;
        DecisionRemarks = remarks.Value;
        Touch(now);
        RaiseDomainEvent(new AccountApplicationRejectedDomainEvent(previous, officerUserId, remarks.Value, now));
    }

    public void PlaceOnHold(string officerUserId, OfficerText? reason, DateTimeOffset now)
    {
        if (reason is null)
            throw new DomainRuleViolationException("A reason is required to put an account application on hold.");

        TakeForAction(officerUserId, now, allowOnHold: false);

        Status = AccountApplicationStatus.OnHold;
        HoldReason = reason.Value;
        Touch(now);
        RaiseDomainEvent(new AccountApplicationHoldChangedDomainEvent(true, officerUserId, reason.Value, now));
    }

    public void ReleaseHold(string officerUserId, DateTimeOffset now)
    {
        EnsureOfficerMayAct(officerUserId);

        if (Status != AccountApplicationStatus.OnHold)
            throw new DomainConflictException("The account application is not on hold.");
        if (!IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("Only the officer the application is assigned to can release the hold.");

        Status = AccountApplicationStatus.PendingReview;
        HoldReason = null;
        Touch(now);
        RaiseDomainEvent(new AccountApplicationHoldChangedDomainEvent(false, officerUserId, null, now));
    }

    // ------------------------------------------------------------------ Opening (core banking)

    /// <summary>The core-banking system opened the account.</summary>
    public void RecordAccountOpened(string accountNumber, string bsb, DateTimeOffset now)
    {
        EnsureOpening();
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bsb))
            throw new DomainRuleViolationException("An opened account must have an account number and a BSB.");

        OpeningAttempts++;
        AccountNumber = accountNumber.Trim();
        OpenedAt = now;
        NextOpeningAt = null;
        LastOpeningError = null;
        Status = AccountApplicationStatus.Opened;
        Touch(now);
        RaiseDomainEvent(new AccountOpenedDomainEvent(AccountNumber, bsb.Trim(), Product!.Value, now));
    }

    /// <summary>
    /// The core-banking system could not be reached or failed: retry at
    /// <paramref name="nextAttemptAt"/>. After <paramref name="maxAttempts"/> failures the
    /// opening is given up (FAILED). A technical failure never counts as an opened account.
    /// </summary>
    public void RecordOpeningFailure(string error, int maxAttempts, DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        EnsureOpening();

        OpeningAttempts++;
        LastOpeningError = Truncate(string.IsNullOrWhiteSpace(error) ? "Account opening failed." : error.Trim(), 2000);

        if (OpeningAttempts >= maxAttempts)
        {
            Fail($"The core-banking system did not open the account after {OpeningAttempts} attempts. Last error: {LastOpeningError}", now);
            return;
        }

        NextOpeningAt = nextAttemptAt;
        Touch(now);
    }

    /// <summary>The core-banking system refused the opening (a permanent, business-level answer).</summary>
    public void RecordOpeningRefused(string reason, DateTimeOffset now)
    {
        EnsureOpening();

        OpeningAttempts++;
        LastOpeningError = Truncate(string.IsNullOrWhiteSpace(reason) ? "Refused by the core-banking system." : reason.Trim(), 2000);
        Fail($"The core-banking system refused to open the account: {LastOpeningError}", now);
    }

    // ------------------------------------------------------------------ Helpers

    private void Fail(string reason, DateTimeOffset now)
    {
        Status = AccountApplicationStatus.Failed;
        FailureReason = Truncate(reason, 2000);
        NextOpeningAt = null;
        Touch(now);
        RaiseDomainEvent(new AccountOpeningFailedDomainEvent(FailureReason, OpeningAttempts, now));
    }

    private void EnsureOpening()
    {
        if (Status != AccountApplicationStatus.Opening)
            throw new DomainConflictException("The account application is not being opened.");
    }

    /// <summary>SoD + state + ReBAC checks shared by every officer decision; assigns an unassigned application.</summary>
    private void TakeForAction(string officerUserId, DateTimeOffset now, bool allowOnHold)
    {
        EnsureOfficerMayAct(officerUserId);

        if (IsDecided)
            throw new DomainConflictException("The account application is already decided.");
        if (Status == AccountApplicationStatus.OnHold && !allowOnHold)
            throw new DomainConflictException("The application is on hold; release the hold first.");
        if (AssignedOfficerUserId is not null && !IsAssignedTo(officerUserId))
            throw new AssignmentViolationException("The account application is assigned to another officer; only that officer can act on it.");

        if (AssignedOfficerUserId is null)
            AssignTo(officerUserId, now);
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
            throw new SeparationOfDutiesViolationException("The workflow initiator cannot take or decide the account application.");

        if (Same(ComplianceApprovedByUserId, officerUserId))
            throw new SeparationOfDutiesViolationException(
                "The officer who approved this application in Compliance cannot also open its account.");
    }

    private void AssignTo(string officerUserId, DateTimeOffset now)
    {
        AssignedOfficerUserId = officerUserId;
        RaiseDomainEvent(new AccountApplicationAssignedDomainEvent(officerUserId, now));
    }

    private static void RequireOfficer(string officerUserId)
    {
        if (string.IsNullOrWhiteSpace(officerUserId))
            throw new DomainRuleViolationException("The officer must be identified.");
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}