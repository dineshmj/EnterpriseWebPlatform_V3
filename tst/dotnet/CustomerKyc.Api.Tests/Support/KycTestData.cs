using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

/// <summary>
/// Known people and cases for the KYC domain tests - built through the aggregate's own
/// public methods, as the API builds them.
/// </summary>
internal static class KycTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public const string Sophie = "sub-sophie";   // customer service: started the onboarding
    public const string Liam = "sub-liam";       // KYC officer
    public const string Noah = "sub-noah";       // another KYC officer

    public static readonly BranchCode Sydney = BranchCode.Create("SYD001");

    public static Applicant Ava() =>
        Applicant.Create("Ava", "Nguyen", "1 George St", null, "Sydney", "NSW", "2000", "au");

    /// <summary>A KYC case in the shared queue: both stages waiting for review.</summary>
    public static KycCase PendingCase(string? initiatedBy = Sophie) =>
        KycCase.Open(
            Guid.NewGuid(), "APP-261028-TEST01", "CUST-100001", Sydney, Ava(),
            identityProofDocumentId: Guid.NewGuid(), taxProofDocumentId: Guid.NewGuid(), initiatedBy, Now);

    public static DecisionRemarks Remarks(string text = "Name on the passport differs from the application.") =>
        DecisionRemarks.From(text)!;
}