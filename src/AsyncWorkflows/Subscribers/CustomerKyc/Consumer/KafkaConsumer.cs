using Microsoft.Extensions.Options;

using Confluent.Kafka;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;

public sealed class KafkaConsumer : IKafkaConsumer
{
    private readonly IConsumer<string, string> _consumer;

    public KafkaConsumer(
        IOptions<KafkaOptions> options,
        IOptions<CustomerKycSubscriberOptions> subscriberOptions)
    {
        var configuration = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = subscriberOptions.Value.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        _consumer = new ConsumerBuilder<string, string>(configuration).Build();
    }

    public async Task ConsumeAsync(
        string topic,
        string groupId,
        Func<string, string, CancellationToken, Task> messageHandler,
        CancellationToken cancellationToken)
    {
        _consumer.Subscribe(topic);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = _consumer.Consume(cancellationToken);

                    if (result?.Message is null)
                    {
                        continue;
                    }

                    await messageHandler(
                        result.Message.Key ?? string.Empty,
                        result.Message.Value,
                        cancellationToken);

                    _consumer.Commit(result);
                }
                catch (ConsumeException ex)
                {
                    throw new InvalidOperationException(
                        $"Kafka consume error: {ex.Error.Reason}",
                        ex);
                }
            }
        }
        finally
        {
            _consumer.Close();
        }
    }

    public void Dispose()
    {
        _consumer.Dispose();
    }
}