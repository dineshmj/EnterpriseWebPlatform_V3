using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Common;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.Events;

/// <summary>A KYC case was opened for an onboarding application.</summary>
public sealed record KycCaseOpenedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An officer decided one verification stage.</summary>
public sealed record VerificationStageDecidedDomainEvent(
    VerificationStageType Stage,
    VerificationStatus PreviousStatus,
    VerificationStatus NewStatus,
    string DecidedByUserId,
    string? Remarks,
    KycCaseStatus OverallStatus,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An officer took the case (claim or first decision). Internal: not published.</summary>
public sealed record KycCaseAssignedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The assigned officer returned the case to the work queue. Internal: not published.</summary>
public sealed record KycCaseReleasedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The case reached its final outcome (APPROVED or REJECTED).</summary>
public sealed record KycCaseDecidedDomainEvent(
    KycCaseStatus PreviousStatus,
    KycCaseStatus NewStatus,
    string DecidedByUserId,
    string? Remarks,
    DateTimeOffset OccurredAt) : IDomainEvent;