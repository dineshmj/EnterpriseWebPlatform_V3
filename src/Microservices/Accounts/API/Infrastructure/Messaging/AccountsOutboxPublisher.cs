using System.Diagnostics;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Confluent.Kafka;

using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;

/// <summary>One long-lived, idempotent producer, authenticated as the Accounts API's own Kafka user.</summary>
public sealed class AccountsKafkaProducer(Microsoft.Extensions.Options.IOptions<KafkaOptions> options) : IDisposable
{
    public IProducer<string, string> Producer { get; } =
        new ProducerBuilder<string, string>(KafkaClientSecurity.Apply(new ProducerConfig
        {
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
            MessageTimeoutMs = 30_000
        }, options.Value)).Build();

    public void Dispose()
    {
        Producer.Flush(TimeSpan.FromSeconds(5));
        Producer.Dispose();
    }
}

/// <summary>
/// Relays committed Accounts Outbox rows to Kafka with the platform's relay
/// guarantees: FOR UPDATE SKIP LOCKED (multi-instance safe), per-aggregate ordering,
/// bounded attempts with exponential back-off, parking, and trace continuation.
/// </summary>
public sealed class AccountsOutboxPublisher(
    AccountsDbContext db,
    AccountsKafkaProducer kafka,
    ILogger<AccountsOutboxPublisher> logger)
{
    public const int MaxAttempts = 10;
    private const double BaseDelaySeconds = 5;
    private const int BatchSize = 50;

    private static readonly Dictionary<string, string> TopicByEventType = new(StringComparer.Ordinal)
    {
        [AccountsIntegrationEventMapper.AccountApplicationCreated] = KafkaTopicNames.AccountApplicationCreated,
        [AccountsIntegrationEventMapper.AccountApplicationRejected] = KafkaTopicNames.AccountApplicationRejected,
        [AccountsIntegrationEventMapper.AccountOpened] = KafkaTopicNames.AccountOpened,
        [AccountsIntegrationEventMapper.AccountOpeningFailed] = KafkaTopicNames.AccountOpeningFailed,

        // Replies to the Payments saga orchestrator's funds commands.
        [FundsIntegrationEventMapper.FundsReserved] = KafkaTopicNames.AccountsFundsReplies,
        [FundsIntegrationEventMapper.FundsReservationFailed] = KafkaTopicNames.AccountsFundsReplies,
        [FundsIntegrationEventMapper.FundsSettled] = KafkaTopicNames.AccountsFundsReplies,
        [FundsIntegrationEventMapper.FundsReleased] = KafkaTopicNames.AccountsFundsReplies
    };

    public static async Task<OutboxBacklog> GetBacklogAsync(AccountsDbContext db, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<OutboxBacklog>($"""
            SELECT
                count(*) FILTER (WHERE attempt_count >= {MaxAttempts}) AS "Parked",
                count(*) FILTER (WHERE attempt_count < {MaxAttempts}) AS "Pending",
                EXTRACT(EPOCH FROM now() - min(occurred_at) FILTER (WHERE attempt_count < {MaxAttempts}))::float8 AS "OldestPendingSeconds"
            FROM outbox_messages
            WHERE published_at IS NULL
            """).ToListAsync(ct);
        return rows.Single();
    }

    public async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        var eventTypes = TopicByEventType.Keys.ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var messages = await db.OutboxMessages
            .FromSql($"""
                SELECT o.*
                FROM outbox_messages o
                WHERE o.published_at IS NULL
                  AND o.event_type = ANY({eventTypes})
                  AND o.attempt_count < {MaxAttempts}
                  AND (o.last_attempt_at IS NULL
                       OR o.last_attempt_at + make_interval(secs => {BaseDelaySeconds} * power(2, greatest(o.attempt_count - 1, 0))) <= now())
                  AND NOT EXISTS (
                      SELECT 1 FROM outbox_messages earlier
                      WHERE earlier.aggregate_type = o.aggregate_type
                        AND earlier.aggregate_id = o.aggregate_id
                        AND earlier.published_at IS NULL
                        AND earlier.sequence < o.sequence)
                ORDER BY o.sequence
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in messages)
        {
            var topic = TopicByEventType[message.EventType];
            var attemptedAt = DateTimeOffset.UtcNow;

            // The publish span continues the trace of the request that raised the event.
            using var activity = MessagingTelemetry.StartPublish(topic, message.Id, message.EventType, message.TraceParent);
            try
            {
                var headers = new Headers();
                foreach (var (name, value) in MessagingTelemetry.PublishHeaders(
                             activity, message.TraceParent, message.Id, message.EventType,
                             message.WorkflowId, message.CorrelationId, message.CausationId))
                {
                    headers.Add(name, Encoding.UTF8.GetBytes(value));
                }

                await kafka.Producer.ProduceAsync(topic, new Message<string, string>
                {
                    Key = message.AggregateId,
                    Value = message.Payload,
                    Headers = headers
                }, ct);

                message.PublishedAt = attemptedAt;
                message.LastAttemptAt = attemptedAt;
                message.LastError = null;
                message.AttemptCount++;
                published++;
                logger.LogInformation("Published Accounts outbox message {MessageId} {EventType} to {Topic}.", message.Id, message.EventType, topic);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                message.AttemptCount++;
                message.LastAttemptAt = attemptedAt;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                logger.Log(
                    message.AttemptCount >= MaxAttempts ? LogLevel.Error : LogLevel.Warning, ex,
                    message.AttemptCount >= MaxAttempts
                        ? "Accounts outbox message {MessageId} {EventType} failed {AttemptCount} times and is now PARKED."
                        : "Failed to publish Accounts outbox message {MessageId} {EventType}. Attempt {AttemptCount}.",
                    message.Id, message.EventType, message.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return published;
    }
}

public sealed class AccountsOutboxPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices(AccountsOutboxPublisherHostedService.HeartbeatKey)] LoopHeartbeat heartbeat,
    ILogger<AccountsOutboxPublisherHostedService> logger) : BackgroundService
{
    public const string HeartbeatKey = "accounts-outbox-relay";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var publisher = scope.ServiceProvider.GetRequiredService<AccountsOutboxPublisher>();
                for (var i = 0; i < 10 && await publisher.PublishBatchAsync(stoppingToken) > 0; i++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Accounts outbox publisher cycle failed."); }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}