namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public static class KafkaTopicNames
{
    public const string CustomerCreated = "customer.created";

    public const string OnboardingApplicationSubmitted =
        "onboarding.application.submitted";

    public const string OnboardingApplicationStatusChanged =
        "onboarding.application.status.changed";
}
