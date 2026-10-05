namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Published when a deciding context (KYC, Compliance or Accounts) rejects an onboarding
/// application: the business failure of the onboarding saga. Each interested
/// context compensates its own state; Documents Management invalidates (retains,
/// never deletes) exactly the evidence documents listed here.
///
/// <c>RejectedBy</c> is <c>KYC</c>, <c>COMPLIANCE</c> or <c>ACCOUNTS</c>. <c>BranchCode</c> is the
/// application's branch: DM invalidates only documents of that branch.
/// </summary>
public sealed record OnboardingApplicationRejectedIntegrationEvent(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string RejectedBy,
    string PreviousStatus,
    IReadOnlyList<EvidenceDocumentReference> EvidenceDocuments);

public sealed record EvidenceDocumentReference(Guid DocumentId, string DocumentType);