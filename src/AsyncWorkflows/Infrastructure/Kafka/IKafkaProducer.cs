namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public interface IKafkaProducer
{
    Task ProduceAsync(
        string topic,
        string key,
        string payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Produces a message with Kafka headers (e.g. dead-letter diagnostics:
    /// original topic/partition/offset and the failure reason).
    /// </summary>
    Task ProduceAsync(
        string topic,
        string key,
        string payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken);
}