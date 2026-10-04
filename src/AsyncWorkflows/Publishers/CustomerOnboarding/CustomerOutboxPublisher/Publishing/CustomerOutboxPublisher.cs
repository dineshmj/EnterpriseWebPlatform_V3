using EnterpriseWebPlatform.Common.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;

/// <summary>
/// Relays committed Customer Onboarding Outbox rows to Kafka.
///
/// Guarantees:
/// - Multi-instance safe: rows are claimed with FOR UPDATE SKIP LOCKED inside a
///   transaction, so two relay instances never publish the same row.
/// - Per-aggregate ordering: only the oldest unpublished message of an aggregate
///   is eligible. A failing message holds back later messages of the same
///   aggregate, but never those of other aggregates.
/// - Bounded retries with exponential backoff; after MaxAttempts a message is
///   parked for operational recovery instead of being retried forever.
/// - Unknown event types are never selected, so they cannot block the relay.
/// </summary>
public sealed class CustomerOutboxPublisher(
    CustomerOutboxDbContext dbContext,
    IKafkaProducer kafkaProducer,
    IOptions<CustomerOutboxPublisherOptions> options,
    ILogger<CustomerOutboxPublisher> logger)
    : ICustomerOutboxPublisher
{
    // Only event types with an explicit Kafka contract are ever selected.
    private static readonly IReadOnlyDictionary<string, string> TopicByEventType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerCreated"] = KafkaTopicNames.CustomerCreated,
            ["OnboardingApplicationSubmitted"] = KafkaTopicNames.OnboardingApplicationSubmitted,
            ["OnboardingApplicationStatusChanged"] = KafkaTopicNames.OnboardingApplicationStatusChanged
        };

    private const int MaxCyclesPerPoll = 10;

    private readonly CustomerOutboxPublisherOptions _options = options.Value;

    public async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        // Several claim cycles per poll, so consecutive events of one aggregate
        // (e.g. Submitted followed by StatusChanged) do not each wait a full
        // poll interval.
        for (var cycle = 0; cycle < MaxCyclesPerPoll; cycle++)
        {
            var published = await PublishBatchAsync(cancellationToken);
            if (published == 0)
                break;
        }
    }

    /// <summary>Unpublished rows, for the readiness check (parked = given up after MaxAttempts).</summary>
    public static async Task<OutboxBacklog> GetBacklogAsync(CustomerOutboxDbContext db, int maxAttempts, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<OutboxBacklog>($"""
                SELECT
                    count(*) FILTER (WHERE attempt_count >= {maxAttempts}) AS "Parked",
                    count(*) FILTER (WHERE attempt_count < {maxAttempts}) AS "Pending",
                    EXTRACT(EPOCH FROM now() - min(occurred_at) FILTER (WHERE attempt_count < {maxAttempts}))::float8 AS "OldestPendingSeconds"
                FROM outbox_messages
                WHERE published_at IS NULL
                """).ToListAsync(ct);
        return rows.Single();
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var eventTypes = TopicByEventType.Keys.ToArray();
        var maxAttempts = _options.MaxAttempts;
        var baseDelaySeconds = (double)_options.RetryBaseDelaySeconds;
        var batchSize = _options.BatchSize;

        var messages = await dbContext.OutboxMessages
            .FromSql($"""
                SELECT o.*
                FROM outbox_messages o
                WHERE o.published_at IS NULL
                  AND o.event_type = ANY({eventTypes})
                  AND o.attempt_count < {maxAttempts}
                  AND (o.last_attempt_at IS NULL
                       OR o.last_attempt_at
                          + make_interval(secs => {baseDelaySeconds} * power(2, greatest(o.attempt_count - 1, 0)))
                          <= now())
                  AND NOT EXISTS (
                      SELECT 1
                      FROM outbox_messages earlier
                      WHERE earlier.aggregate_type = o.aggregate_type
                        AND earlier.aggregate_id = o.aggregate_id
                        AND earlier.published_at IS NULL
                        AND earlier.sequence < o.sequence)
                ORDER BY o.sequence
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var publishedCount = 0;

        foreach (var message in messages)
        {
            var topic = TopicByEventType[message.EventType];
            var attemptedAt = DateTimeOffset.UtcNow;

            // The publish span continues the trace of the request that raised the event.
            using var activity = MessagingTelemetry.StartPublish(topic, message.Id, message.EventType, message.TraceParent);

            try
            {
                if (message.InitiatedByUserId is null)
                {
                    logger.LogWarning(
                        "Outbox message {MessageId} has no InitiatedByUserId. " +
                        "The event will be published, but downstream user-targeted " +
                        "notifications cannot be routed to a human initiator.",
                        message.Id);
                }

                await kafkaProducer.ProduceAsync(
                    topic,
                    message.AggregateId,
                    message.Payload,
                    MessagingTelemetry.PublishHeaders(
                        activity, message.TraceParent, message.Id, message.EventType,
                        message.WorkflowId, message.CorrelationId, message.CausationId),
                    cancellationToken);

                message.PublishedAt = attemptedAt;
                message.LastAttemptAt = attemptedAt;
                message.LastError = null;
                message.AttemptCount++;
                publishedCount++;

                logger.LogInformation(
                    "Published Outbox message {MessageId} EventType={EventType} " +
                    "for aggregate {AggregateId} to topic {Topic}. InitiatedByUserId={InitiatedByUserId}.",
                    message.Id,
                    message.EventType,
                    message.AggregateId,
                    topic,
                    message.InitiatedByUserId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                message.AttemptCount++;
                message.LastAttemptAt = attemptedAt;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                if (message.AttemptCount >= _options.MaxAttempts)
                {
                    logger.LogError(
                        ex,
                        "Outbox message {MessageId} EventType={EventType} for aggregate {AggregateId} " +
                        "failed {AttemptCount} times and is now PARKED. Later messages of this aggregate " +
                        "are held back until it is resolved.",
                        message.Id,
                        message.EventType,
                        message.AggregateId,
                        message.AttemptCount);
                }
                else
                {
                    logger.LogWarning(
                        ex,
                        "Failed to publish Outbox message {MessageId} EventType={EventType} " +
                        "for aggregate {AggregateId} to topic {Topic}. Attempt {AttemptCount}/{MaxAttempts}.",
                        message.Id,
                        message.EventType,
                        message.AggregateId,
                        topic,
                        message.AttemptCount,
                        _options.MaxAttempts);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return publishedCount;
    }
}
