namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>The verifying context whose decision rejected an onboarding application.</summary>
public enum OnboardingRejectionStage
{
    Kyc = 1,
    Compliance = 2
}

public static class OnboardingRejectionStageCode
{
    /// <summary>Published code (integration contract): <c>KYC</c> or <c>COMPLIANCE</c>.</summary>
    public static string ToCode(this OnboardingRejectionStage stage) => stage switch
    {
        OnboardingRejectionStage.Kyc => "KYC",
        OnboardingRejectionStage.Compliance => "COMPLIANCE",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}