namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = string.Empty;
}