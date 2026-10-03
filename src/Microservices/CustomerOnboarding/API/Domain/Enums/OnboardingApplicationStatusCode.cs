namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>
/// The persisted / published code of an <see cref="OnboardingApplicationStatus"/>:
/// UPPER_SNAKE_CASE (e.g. KYC_IN_PROGRESS), matching ck_onboarding_applications_status
/// and the Integration Event Catalogue's enum convention. Plain ToUpperInvariant()
/// would produce KYCINPROGRESS for multi-word members.
/// </summary>
public static class OnboardingApplicationStatusCode
{
    public static string ToCode(this OnboardingApplicationStatus status) => status switch
    {
        OnboardingApplicationStatus.Draft => "DRAFT",
        OnboardingApplicationStatus.Submitted => "SUBMITTED",
        OnboardingApplicationStatus.KycInProgress => "KYC_IN_PROGRESS",
        OnboardingApplicationStatus.KycCompleted => "KYC_COMPLETED",
        OnboardingApplicationStatus.ComplianceInProgress => "COMPLIANCE_IN_PROGRESS",
        OnboardingApplicationStatus.ComplianceCompleted => "COMPLIANCE_COMPLETED",
        OnboardingApplicationStatus.AccountOpeningInProgress => "ACCOUNT_OPENING_IN_PROGRESS",
        OnboardingApplicationStatus.Completed => "COMPLETED",
        OnboardingApplicationStatus.Rejected => "REJECTED",
        OnboardingApplicationStatus.Cancelled => "CANCELLED",
        OnboardingApplicationStatus.Compensating => "COMPENSATING",
        OnboardingApplicationStatus.CompensationFailed => "COMPENSATION_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown onboarding application status.")
    };

    public static OnboardingApplicationStatus FromCode(string code) =>
        Enum.Parse<OnboardingApplicationStatus>(code.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);
}
