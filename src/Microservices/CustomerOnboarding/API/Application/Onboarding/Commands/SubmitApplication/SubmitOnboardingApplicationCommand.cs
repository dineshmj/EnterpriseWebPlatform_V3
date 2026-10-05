namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.SubmitApplication;

public sealed record SubmitOnboardingApplicationCommand(
    long ApplicationId,
    long ExpectedVersion,
    IReadOnlyList<SubmittedEvidenceDocument> EvidenceDocuments);

/// <summary>A Documents Management document submitted as evidence with the application.</summary>
public sealed record SubmittedEvidenceDocument(Guid DocumentId, string DocumentType);