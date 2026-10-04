using System.Text;

using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

/// <summary>
/// The reliable consume loop shared by every subscriber. The worker supplies only
/// an <see cref="IMessageProcessor"/>; delivery behaviour is the same everywhere:
///
///  - Parallel instances share one consumer group; Kafka gives each partition to
///    exactly one instance, so no two instances process the same message at once.
///  - The offset is committed only AFTER a message is processed or dead-lettered
///    (at-least-once). A redelivery after a crash or rebalance is harmless: the
///    owning API is idempotent per MessageId (Inbox).
///  - Transient failures are retried IN PLACE with back-off (the consumer seeks
///    back to the same offset), so the message is never skipped and the order
///    within the partition is preserved.
///  - Permanent failures go to the dead-letter topic with diagnostic headers and
///    are then committed, so one bad message never blocks the partition or stops
///    the worker.
/// </summary>
public sealed class KafkaSubscriberHostedService<TProcessor, TSettings>(
    TProcessor processor,
    IKafkaProducer producer,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<TSettings> settings,
    ILogger<KafkaSubscriberHostedService<TProcessor, TSettings>> logger)
    : BackgroundService
    where TProcessor : IMessageProcessor
    where TSettings : class, ISubscriberSettings
{
    private readonly TSettings _settings = settings.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Confluent's Consume() blocks; keep it off the host's start-up thread.
        Task.Run(() => ConsumeLoopAsync(stoppingToken), stoppingToken);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.Value.BootstrapServers,
            GroupId = _settings.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                logger.LogInformation("Partitions assigned: {Partitions}", string.Join(", ", partitions)))
            .SetPartitionsRevokedHandler((_, partitions) =>
                logger.LogInformation("Partitions revoked: {Partitions}", string.Join(", ", partitions)))
            .Build();

        consumer.Subscribe(_settings.Topics);
        logger.LogInformation(
            "{Subscriber} started. Topics={Topics}, GroupId={GroupId}, DeadLetterTopic={DeadLetterTopic}",
            typeof(TProcessor).Name,
            string.Join(", ", _settings.Topics),
            _settings.GroupId,
            _settings.DeadLetterTopic);

        var transientAttempts = 0;
        TopicPartitionOffset? retrying = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    // e.g. a subscribed topic does not exist yet: keep running.
                    logger.LogWarning("Kafka consume error (non-fatal): {Reason}", ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                    continue;

                if (retrying is null || !retrying.Equals(result.TopicPartitionOffset))
                    transientAttempts = 0;

                try
                {
                    var outcome = await processor.ProcessAsync(ToConsumedMessage(result), stoppingToken);

                    if (outcome.DeadLetter)
                        await DeadLetterAsync(result, outcome.Reason ?? "Unprocessable message.", stoppingToken);

                    consumer.Commit(result);
                    retrying = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Transient (dependency down) or unexpected: never lose the
                    // message. Rewind to it and try again after a back-off.
                    transientAttempts++;
                    retrying = result.TopicPartitionOffset;

                    var delay = TimeSpan.FromSeconds(Math.Min(
                        _settings.TransientRetryMaxDelaySeconds,
                        _settings.TransientRetryInitialDelaySeconds * Math.Pow(2, Math.Min(transientAttempts - 1, 10))));

                    logger.LogWarning(
                        ex,
                        "Could not process {Topic} [{Partition}] @{Offset} (attempt {Attempt}); retrying the same message in {Delay}.",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value,
                        transientAttempts,
                        delay);

                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            consumer.Close();   // leave the group cleanly so partitions are reassigned at once
            logger.LogInformation("{Subscriber} stopped.", typeof(TProcessor).Name);
        }
    }

    private static ConsumedMessage ToConsumedMessage(ConsumeResult<string, string> result)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (result.Message.Headers is not null)
        {
            foreach (var header in result.Message.Headers)
                headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
        }

        return new ConsumedMessage(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            result.Message.Key,
            result.Message.Value,
            headers);
    }

    private async Task DeadLetterAsync(
        ConsumeResult<string, string> result,
        string reason,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            ["dlq-reason"] = reason.Length > 1000 ? reason[..1000] : reason,
            ["dlq-original-topic"] = result.Topic,
            ["dlq-original-partition"] = result.Partition.Value.ToString(),
            ["dlq-original-offset"] = result.Offset.Value.ToString(),
            ["dlq-consumer-group"] = _settings.GroupId,
            ["dlq-failed-at"] = DateTimeOffset.UtcNow.ToString("O")
        };

        // If this produce fails, the exception propagates as transient: the
        // message is retried rather than committed without being parked.
        await producer.ProduceAsync(
            _settings.DeadLetterTopic,
            result.Message.Key ?? string.Empty,
            result.Message.Value,
            headers,
            cancellationToken);

        logger.LogError(
            "Message {Topic} [{Partition}] @{Offset} moved to dead-letter topic {DeadLetterTopic}: {Reason}",
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            _settings.DeadLetterTopic,
            reason);
    }
}
