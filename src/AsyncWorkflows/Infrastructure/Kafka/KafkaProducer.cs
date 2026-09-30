using Microsoft.Extensions.Options;

using Confluent.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        var configuration = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers
        };

        _producer = new ProducerBuilder<string, string>(configuration).Build();
    }

    public async Task ProduceAsync(
        string topic,
        string key,
        string payload,
        CancellationToken cancellationToken)
    {
        await _producer.ProduceAsync(
            topic,
            new Message<string, string>
            {
                Key = key,
                Value = payload
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}