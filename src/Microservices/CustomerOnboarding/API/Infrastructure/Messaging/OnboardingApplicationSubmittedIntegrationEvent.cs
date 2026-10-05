namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Published when an onboarding application is submitted. Starts the KYC step of
/// the onboarding saga: Customer KYC opens one KYC case per application.
///
/// Other contexts must identify the application by <c>ApplicationRef</c> (a GUID
/// that never repeats); <c>ApplicationId</c> is Customer Onboarding's internal
/// database ID and restarts when its database is recreated. <c>BranchCode</c> is
/// the branch the application was opened in (KYC scopes its work queue by it).
/// CustomerNumber lets KYC locate the customer's evidence in Documents Management.
/// <c>EvidenceDocuments</c> are the documents submitted with the application (added
/// in schema version 1, additively): Documents Management attaches them, so they are
/// retained and can no longer be deleted.
/// <c>Applicant</c> is the applicant as submitted (name and primary residential address;
/// added additively): KYC verifies the evidence against it and passes it on. Contact
/// details are deliberately not published - no other context needs them.
/// </summary>
public sealed record OnboardingApplicationSubmittedIntegrationEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    IReadOnlyList<EvidenceDocumentReference> EvidenceDocuments,
    ApplicantReference Applicant);

public sealed record ApplicantReference(string FirstName, string LastName, AddressReference? ResidentialAddress);

public sealed record AddressReference(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string CountryCode);