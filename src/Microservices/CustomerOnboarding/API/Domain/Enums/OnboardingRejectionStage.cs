namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>
/// Where an onboarding application ended unsuccessfully: a deciding context's decision
/// (KYC, Compliance, Accounts), or the account opening failing after every approval
/// (a technical end that the saga compensates, not an officer's decision).
/// </summary>
public enum OnboardingRejectionStage
{
    Kyc = 1,
    Compliance = 2,
    Accounts = 3,
    AccountOpening = 4
}

public static class OnboardingRejectionStageCode
{
    /// <summary>Published code (integration contract): <c>KYC</c>, <c>COMPLIANCE</c>, <c>ACCOUNTS</c> or <c>ACCOUNT_OPENING</c>.</summary>
    public static string ToCode(this OnboardingRejectionStage stage) => stage switch
    {
        OnboardingRejectionStage.Kyc => "KYC",
        OnboardingRejectionStage.Compliance => "COMPLIANCE",
        OnboardingRejectionStage.Accounts => "ACCOUNTS",
        OnboardingRejectionStage.AccountOpening => "ACCOUNT_OPENING",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}