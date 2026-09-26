namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;

public sealed class CustomerOutboxPublisherOptions
{
    public const string SectionName = "CustomerOutboxPublisher";

    public int BatchSize { get; init; } = 50;

    public int PollIntervalSeconds { get; init; } = 10;
}
