using System.Diagnostics.Metrics;

using Microsoft.Extensions.Options;

using Confluent.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    // ewp_messaging_published_total{topic,outcome}: every relay and every dead-letter write
    // goes through here. Same meter name as the subscriber side (MessagingTelemetry).
    private static readonly Meter Meter = new("EnterpriseWebPlatform.Messaging");
    private static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "ewp.messaging.published", description: "Kafka messages published, by outcome.");

    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        var configuration = KafkaClientSecurity.Apply(new ProducerConfig
        {
            // A publish is acknowledged only when all in-sync replicas have it,
            // and broker-side de-duplication prevents duplicates and reordering
            // caused by the producer's own internal retries.
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
            MessageTimeoutMs = 30_000
        }, options.Value);

        _producer = new ProducerBuilder<string, string>(configuration).Build();
    }

    public async Task ProduceAsync(
        string topic,
        string key,
        string payload,
        CancellationToken cancellationToken)
    {
        await CountedAsync(topic, () => _producer.ProduceAsync(
            topic,
            new Message<string, string>
            {
                Key = key,
                Value = payload
            },
            cancellationToken));
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

        await CountedAsync(topic, () => _producer.ProduceAsync(
            topic,
            new Message<string, string>
            {
                Key = key,
                Value = payload,
                Headers = kafkaHeaders
            },
            cancellationToken));
    }

    private static async Task CountedAsync(string topic, Func<Task> produce)
    {
        try
        {
            await produce();
            Published.Add(1, new KeyValuePair<string, object?>("topic", topic), new KeyValuePair<string, object?>("outcome", "ok"));
        }
        catch
        {
            Published.Add(1, new KeyValuePair<string, object?>("topic", topic), new KeyValuePair<string, object?>("outcome", "failed"));
            throw;
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}