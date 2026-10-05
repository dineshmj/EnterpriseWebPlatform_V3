namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>The verifying context whose decision rejected an onboarding application.</summary>
public enum OnboardingRejectionStage
{
    Kyc = 1,
    Compliance = 2,
    Accounts = 3
}

public static class OnboardingRejectionStageCode
{
    /// <summary>Published code (integration contract): <c>KYC</c>, <c>COMPLIANCE</c> or <c>ACCOUNTS</c>.</summary>
    public static string ToCode(this OnboardingRejectionStage stage) => stage switch
    {
        OnboardingRejectionStage.Kyc => "KYC",
        OnboardingRejectionStage.Compliance => "COMPLIANCE",
        OnboardingRejectionStage.Accounts => "ACCOUNTS",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}