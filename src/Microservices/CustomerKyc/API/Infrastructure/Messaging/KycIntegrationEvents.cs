namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Messaging;

// Published contracts (topics kyc.*). These shapes are what consumers read; the
// domain events are internal and translated to these by KycIntegrationEventMapper.
// Every event uses the platform's standard envelope (doc/Integration-Event-Catalogue.md §3):
// workflow metadata at the top level, the event-specific body under Payload.

/// <summary>
/// The standard envelope. SchemaVersion is the contract version of the payload; it
/// changes only for a breaking change (additive fields keep it).
/// </summary>
public sealed record KycIntegrationEventEnvelope<TPayload>(
    Guid MessageId,
    string EventType,
    int SchemaVersion,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? InitiatedByUserId,
    TPayload Payload,
    string? InitiatedByLanId = null);

public sealed record KycCaseCreatedPayload(
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string Status);

public sealed record KycVerificationStageDecisionPayload(
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string Stage,   // "IDENTITY_VERIFICATION" / "DOCUMENT_VERIFICATION" - a stable code, not an enum ordinal
    string PreviousStageStatus,
    string NewStageStatus,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks,
    string OverallStatus,
    string? DecisionByLanId);

/// <remarks>
/// BranchCode and the two stage deciders were added for Compliance (additive, same
/// SchemaVersion): Compliance scopes its case to the branch, and Separation of Duties
/// forbids EITHER KYC officer from approving the same application's compliance case.
/// </remarks>
public sealed record KycCaseDecisionPayload(
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string PreviousStatus,
    string NewStatus,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks,
    IReadOnlyList<Guid> CausedByMessageIds,
    string BranchCode,
    string? IdentityVerificationByUserId,
    string? DocumentVerificationByUserId,
    ApplicantPayload Applicant,
    string? DecisionByLanId,
    string? IdentityVerificationByLanId,
    string? DocumentVerificationByLanId);

/// <summary>
/// The applicant as submitted and verified by KYC (added additively, same SchemaVersion):
/// Compliance screens on the name and address. No contact details are passed on.
/// </summary>
public sealed record ApplicantPayload(string FirstName, string LastName, AddressPayload ResidentialAddress);

public sealed record AddressPayload(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode);