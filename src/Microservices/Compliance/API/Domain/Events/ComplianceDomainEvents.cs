using EnterpriseWebPlatform.Compliance.Api.Domain.Common;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Domain.Events;

/// <summary>A compliance case was opened for a KYC-approved application. Published.</summary>
public sealed record ComplianceCaseOpenedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The screening provider's result was recorded and the risk rated. Internal.</summary>
public sealed record ScreeningCompletedDomainEvent(
    ScreeningOutcome Outcome,
    RiskRating Risk,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An officer took the case (claim or first action). Internal.</summary>
public sealed record ComplianceCaseAssignedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The assigned officer returned the case to the work queue. Internal.</summary>
public sealed record ComplianceCaseReleasedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The case was put on hold, or released from hold. Internal.</summary>
public sealed record ComplianceCaseHoldChangedDomainEvent(bool OnHold, string OfficerUserId, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The case reached its final outcome (APPROVED or REJECTED). Published.</summary>
public sealed record ComplianceCaseDecidedDomainEvent(
    ComplianceCaseStatus PreviousStatus,
    ComplianceCaseStatus NewStatus,
    string DecidedByUserId,
    string? Remarks,
    DateTimeOffset OccurredAt) : IDomainEvent;