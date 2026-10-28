using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Support;

/// <summary>
/// Known people and cases for the Compliance domain tests - built through the aggregate's
/// own public methods, as the API builds them.
/// </summary>
internal static class ComplianceTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public const string Sophie = "sub-sophie";   // customer service: started the onboarding
    public const string Liam = "sub-liam";       // KYC officer: decided identity verification
    public const string Noah = "sub-noah";       // KYC officer: decided document verification
    public const string Olivia = "sub-olivia";   // compliance officer
    public const string Ruby = "sub-ruby";       // another compliance officer

    public static readonly BranchCode Sydney = BranchCode.Create("SYD001");

    /// <summary>A case KYC has just approved: waiting for the screening provider.</summary>
    public static ComplianceCase ScreeningCase(string? initiatedBy = Sophie) =>
        ComplianceCase.Open(
            Guid.NewGuid(), "APP-261028-TEST01", "CUST-100001", kycCaseId: 7, Sydney,
            Applicant.Create("Ava", "Nguyen", "1 George St", null, "Sydney", "NSW", "2000", "AU"),
            initiatedBy, kycIdentityDecidedByUserId: Liam, kycDocumentDecidedByUserId: Noah, Now);

    /// <summary>Screened with this verdict: waiting for a compliance officer.</summary>
    public static ComplianceCase UnderReviewCase(ScreeningOutcome outcome = ScreeningOutcome.Clear, string? initiatedBy = Sophie)
    {
        var complianceCase = ScreeningCase(initiatedBy);
        complianceCase.RecordScreeningResult(outcome, "ScreeningSimulator", "SCR-0001", Now);
        return complianceCase;
    }

    public static OfficerText Text(string text = "Possible match on the PEP list; waiting for the customer's statement.") =>
        OfficerText.From(text)!;
}