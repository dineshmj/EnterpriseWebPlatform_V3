namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public static class KafkaTopicNames
{
    public const string CustomerCreated = "customer.created";

    public const string OnboardingApplicationSubmitted =
        "onboarding.application.submitted";

    public const string OnboardingApplicationStatusChanged =
        "onboarding.application.status.changed";

    public const string KycCaseCreated = "kyc.case.created";

    public const string KycCaseApproved = "kyc.case.approved";

    public const string KycCaseRejected = "kyc.case.rejected";

    /// <summary>
    /// Dead-letter topic of the Customer Onboarding KYC subscriber: messages it can
    /// never process (malformed, unknown, or permanently rejected) are parked here
    /// with diagnostic headers instead of blocking the partition.
    /// </summary>
    public const string CustomerOnboardingKycSubscriberDeadLetter =
        "customer-onboarding.kyc-subscriber.dlq";
}
