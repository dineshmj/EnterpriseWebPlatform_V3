using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Confluent.Kafka;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: RESILIENCE :: Reliable consume loop (at-least-once, in order, dead-letter) ────────────────
// │ Why : A crash, a rebalance or a bad message must neither lose an event, process it twice in a
// │       harmful way, nor stop the partition.
// │ How : Manual commit only AFTER processing or dead-lettering; transient failures are retried in
// │       place (seek back, order kept); permanent ones go to the dead-letter topic with diagnostic
// │       headers, then are committed. The receiving API's Inbox makes a redelivery harmless.
// │       Instances share one consumer group.
// │ Ref : Features 1.3.3, 1.6.1, 1.6.2 | Idempotent Consumer and Dead Letter Channel patterns |
// │       Threat-Model TB5
// └────────────────────────────────────────────────────────────────────────────────────────────────
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
    SubscriberHealth health,
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
        // Authenticates as this worker's own Kafka user (ACLs: its topics and group only).
        var config = KafkaClientSecurity.Apply(new ConsumerConfig
        {
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ No auto-commit: the offset is committed by hand only after processing
            // │ (at-least-once). Earliest: a new consumer group starts at the oldest message, so
            // │ nothing published before its first start is missed.
            // └──────────────────────────────────────────────────────────────────────────────────────
            GroupId = _settings.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = false
        }, kafkaOptions.Value);

        // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
        // │ The Kafka library's own lines and errors go through the logger (KafkaClientLog):
        // │ once per change, not two console lines every few seconds while the broker is down.
        // │ Health still hears of every error.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var clientLog = new KafkaClientLog(logger, "consumer");
        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                health.PartitionsAssigned(partitions.Count);
                logger.LogInformation("Partitions assigned: {Partitions}", string.Join(", ", partitions));
            })
            .SetLogHandler((_, message) => clientLog.OnLog(message))
            .SetErrorHandler((_, error) =>
            {
                // e.g. authentication or authorization failures; the client keeps retrying.
                health.KafkaError(error.Reason);
                clientLog.OnError(error);
            })
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
        health.Started(_settings.GroupId);

        var transientAttempts = 0;
        TopicPartitionOffset? retrying = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    // Poll with a short timeout (instead of blocking until a message
                    // arrives) so the liveness heartbeat also ticks on a quiet topic.
                    health.Heartbeat();
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
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

                var consumed = ToConsumedMessage(result);

                // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
                // │ The process span continues the producer's trace (Kafka "traceparent" header); the
                // │ processor's HttpClient call carries it on to the next API. RecordConsumed counts each
                // │ outcome (processed / dead_lettered / retried) as ewp_messaging_consumed_total.
                // └──────────────────────────────────────────────────────────────────────────────────────
                using var activity = MessagingTelemetry.StartProcess(result.Topic, result.Partition.Value, result.Offset.Value, consumed.Headers);

                try
                {
                    var outcome = await processor.ProcessAsync(consumed, stoppingToken);

                    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
                    // │ If the message can never succeed, copy it to the dead-letter topic with diagnostic
                    // │ headers, then commit - the partition keeps flowing. Otherwise it was processed:
                    // │ commit only now (at-least-once).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    if (outcome.DeadLetter)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, "dead-lettered");
                        await DeadLetterAsync(result, outcome.Reason ?? "Unprocessable message.", stoppingToken);
                    }

                    consumer.Commit(result);
                    retrying = null;
                    health.Processed(outcome.DeadLetter);
                    MessagingTelemetry.RecordConsumed(result.Topic, outcome.DeadLetter ? "dead_lettered" : "processed");
                }
                // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
                // │ If processing failed for a transient reason (the API is down), seek back to the SAME
                // │ offset and retry with back-off - the message is never skipped and the partition's
                // │ order is kept.
                // └──────────────────────────────────────────────────────────────────────────────────────
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    // Transient (dependency down) or unexpected: never lose the
                    // message. Rewind to it and try again after a back-off.
                    transientAttempts++;
                    retrying = result.TopicPartitionOffset;
                    MessagingTelemetry.RecordConsumed(result.Topic, "retried");
                    health.Retrying(result.TopicPartitionOffset.ToString(), transientAttempts, ex.Message);

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

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ If this produce fails, the exception propagates as transient: the message is retried
        // │ rather than committed without being parked.
        // └──────────────────────────────────────────────────────────────────────────────────────
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