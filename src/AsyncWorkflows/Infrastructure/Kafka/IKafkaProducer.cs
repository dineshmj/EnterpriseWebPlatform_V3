namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public interface IKafkaProducer
{
    Task ProduceAsync(
        string topic,
        string key,
        string payload,
        CancellationToken cancellationToken);
}
