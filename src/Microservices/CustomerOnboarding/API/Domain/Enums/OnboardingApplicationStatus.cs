namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

public enum OnboardingApplicationStatus
{
    Draft = 1,
    Submitted = 2,
    KycInProgress = 3,
    KycCompleted = 4,
    ComplianceInProgress = 5,
    ComplianceCompleted = 6,
    AccountOpeningInProgress = 7,
    Completed = 8,
    Rejected = 9,
    Cancelled = 10,
    Compensating = 11,
    CompensationFailed = 12
}