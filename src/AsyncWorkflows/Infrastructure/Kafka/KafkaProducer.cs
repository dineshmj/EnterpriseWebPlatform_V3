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
            BootstrapServers = options.Value.BootstrapServers,

            // A publish is acknowledged only when all in-sync replicas have it,
            // and broker-side de-duplication prevents duplicates and reordering
            // caused by the producer's own internal retries.
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
            MessageTimeoutMs = 30_000
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

    public async Task ProduceAsync(
        string topic,
        string key,
        string payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var kafkaHeaders = new Headers();
        foreach (var (name, value) in headers)
        {
            kafkaHeaders.Add(name, System.Text.Encoding.UTF8.GetBytes(value));
        }

        await _producer.ProduceAsync(
            topic,
            new Message<string, string>
            {
                Key = key,
                Value = payload,
                Headers = kafkaHeaders
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}