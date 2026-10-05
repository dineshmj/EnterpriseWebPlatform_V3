namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Models;

/// <summary>
/// The subscriber's own (tolerant-reader) view of the
/// onboarding.application.submitted integration event: only the fields KYC needs.
/// </summary>
public sealed record ApplicationSubmittedMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? Source,
    ApplicantInfo? Applicant,
    IReadOnlyList<EvidenceDocumentInfo> EvidenceDocuments);

/// <summary>The applicant as submitted (name and residential address); KYC verifies against it.</summary>
public sealed record ApplicantInfo(string? FirstName, string? LastName, AddressInfo? ResidentialAddress);

public sealed record AddressInfo(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode);

/// <summary>A document submitted as evidence: KYC reviews exactly these.</summary>
public sealed record EvidenceDocumentInfo(Guid DocumentId, string? DocumentType);