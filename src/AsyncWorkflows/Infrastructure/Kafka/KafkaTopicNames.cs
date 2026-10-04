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
    /// Dead-letter topic of the Onboarding Outcome Subscriber: messages it can
    /// never process (malformed, unknown, or permanently rejected) are parked here
    /// with diagnostic headers instead of blocking the partition.
    /// The topic keeps its original name (and the worker its consumer group,
    /// "customer-onboarding-kyc-subscriber") so existing messages and committed
    /// offsets stay valid; Kafka names are revisited with the per-topic ACLs.
    /// </summary>
    public const string OnboardingOutcomeSubscriberDeadLetter =
        "customer-onboarding.kyc-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the KYC Case Opening Subscriber (Customer KYC):
    /// onboarding.application.submitted messages that can never open a case.
    /// </summary>
    public const string KycCaseOpeningSubscriberDeadLetter =
        "customer-kyc.case-opening-subscriber.dlq";
}
