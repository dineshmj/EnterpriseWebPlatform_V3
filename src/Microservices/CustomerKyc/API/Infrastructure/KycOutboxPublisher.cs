using Microsoft.EntityFrameworkCore;

using Confluent.Kafka;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

/// <summary>
/// One long-lived, idempotent Kafka producer for the KYC Outbox relay
/// (a producer is expensive to create and must not be built per poll).
/// </summary>
public sealed class KycKafkaProducer : IDisposable
{
    public KycKafkaProducer(IConfiguration config, ILogger<KycKafkaProducer> logger)
    {
        // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
        // │ The Kafka library's own lines and errors go through the logger (KafkaClientLog):
        // │ once per change, not two console lines every few seconds while the broker is down.
        // └──────────────────────────────────────────────────────────────────────────────────────
        // Authenticates as the KYC API's own Kafka user: ACLs allow it to write kyc.* only.
        var kafka = config.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>()
            ?? throw new InvalidOperationException("The Kafka configuration section is missing.");

        Producer = new ProducerBuilder<string, string>(KafkaClientSecurity.Apply(new ProducerConfig
        {
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ Acks=All with idempotence: a publish counts only when every in-sync replica has it,
            // │ and a retried send is never duplicated (see class KafkaProducer).
            // └──────────────────────────────────────────────────────────────────────────────────────
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
            MessageTimeoutMs = 30_000
        }, kafka)).WithClientLog(new KafkaClientLog(logger, "producer")).Build();
    }

    public IProducer<string, string> Producer { get; }

    public void Dispose()
    {
        Producer.Flush(TimeSpan.FromSeconds(5));
        Producer.Dispose();
    }
}

// ┌─ :: RESILIENCE :: Transactional Outbox relay (in-process) ─────────────────────────────────────
// │ Why : KYC outcomes must reach Kafka exactly when their decision commits.
// │ How : FOR UPDATE SKIP LOCKED, per-aggregate order, bounded attempts with back-off, parked after
// │       MaxAttempts; the decision's trace continues into Kafka.
// │ Ref : Features 1.3.1 | Transactional Outbox pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Relays committed KYC Outbox rows to Kafka, with the same guarantees as the
/// Customer Onboarding relay: FOR UPDATE SKIP LOCKED claiming (multi-instance
/// safe), per-aggregate ordering, bounded attempts with exponential backoff,
/// and parking of messages that keep failing. Unknown event types are never
/// selected (previously they were silently sent to the kyc.case.created topic).
/// </summary>
public sealed class KycOutboxPublisher(
    KycDbContext db,
    KycKafkaProducer kafka,
    IConfiguration config,
    ILogger<KycOutboxPublisher> logger)
{
    private const int BatchSize = 50;
    private const int MaxCyclesPerPoll = 10;

    /// <summary>Unpublished rows, for the readiness check (parked = given up after MaxAttempts).</summary>
    public static async Task<OutboxBacklog> GetBacklogAsync(KycDbContext db, IConfiguration config, CancellationToken ct)
    {
        var maxAttempts = config.GetValue("KycOutbox:MaxAttempts", 10);
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

    public async Task PublishPendingAsync(CancellationToken ct)
    {
        var topics = GetTopicMap();

        for (var cycle = 0; cycle < MaxCyclesPerPoll; cycle++)
        {
            if (await PublishBatchAsync(topics, ct) == 0)
                break;
        }
    }

    private Dictionary<string, string> GetTopicMap() => new(StringComparer.Ordinal)
    {
        ["KycCaseCreated"] = config["Kafka:KycCaseCreatedTopic"] ?? "kyc.case.created",
        ["KycCaseApproved"] = config["Kafka:KycCaseApprovedTopic"] ?? "kyc.case.approved",
        ["KycCaseRejected"] = config["Kafka:KycCaseRejectedTopic"] ?? "kyc.case.rejected",
        ["KycIdentityVerificationApproved"] =
            config["Kafka:KycIdentityVerificationApprovedTopic"] ?? "kyc.identity.verification.approved",
        ["KycIdentityVerificationRejected"] =
            config["Kafka:KycIdentityVerificationRejectedTopic"] ?? "kyc.identity.verification.rejected",
        ["KycDocumentVerificationApproved"] =
            config["Kafka:KycDocumentVerificationApprovedTopic"] ?? "kyc.document.verification.approved",
        ["KycDocumentVerificationRejected"] =
            config["Kafka:KycDocumentVerificationRejectedTopic"] ?? "kyc.document.verification.rejected"
    };

    private async Task<int> PublishBatchAsync(IReadOnlyDictionary<string, string> topics, CancellationToken ct)
    {
        var maxAttempts = config.GetValue("KycOutbox:MaxAttempts", 10);
        var baseDelaySeconds = (double)config.GetValue("KycOutbox:RetryBaseDelaySeconds", 5);
        var eventTypes = topics.Keys.ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var messages = await db.OutboxMessages
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
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var published = 0;

        foreach (var message in messages)
        {
            var topic = topics[message.EventType];
            var attemptedAt = DateTimeOffset.UtcNow;

            // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
            // │ The publish span continues the trace of the request that raised the event.
            // └──────────────────────────────────────────────────────────────────────────────────────
            using var activity = MessagingTelemetry.StartPublish(topic, message.Id, message.EventType, message.TraceParent);

            try
            {
                var headers = new Headers();
                foreach (var (name, value) in MessagingTelemetry.PublishHeaders(
                             activity, message.TraceParent, message.Id, message.EventType,
                             message.WorkflowId, message.CorrelationId, message.CausationId))
                {
                    headers.Add(name, System.Text.Encoding.UTF8.GetBytes(value));
                }

                await kafka.Producer.ProduceAsync(
                    topic,
                    new Message<string, string>
                    {
                        Key = message.AggregateId,
                        Value = message.Payload,
                        Headers = headers
                    },
                    ct);

                message.PublishedAt = attemptedAt;
                message.LastAttemptAt = attemptedAt;
                message.LastError = null;
                message.AttemptCount++;
                published++;

                logger.LogInformation(
                    "Published KYC outbox message {MessageId} EventType={EventType} to {Topic}.",
                    message.Id,
                    message.EventType,
                    topic);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                message.AttemptCount++;
                message.LastAttemptAt = attemptedAt;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                logger.Log(
                    message.AttemptCount >= maxAttempts ? LogLevel.Error : LogLevel.Warning,
                    ex,
                    message.AttemptCount >= maxAttempts
                        ? "KYC outbox message {MessageId} EventType={EventType} failed {AttemptCount} times and is now PARKED."
                        : "Failed to publish KYC outbox message {MessageId} EventType={EventType}. Attempt {AttemptCount}.",
                    message.Id,
                    message.EventType,
                    message.AttemptCount);
            }
        }

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ If the relay dies after a publish but before this commit, the row is published again
        // │ on the next run (at-least-once); the consumers' Inbox (MessageId) makes the repeat
        // │ harmless.
        // └──────────────────────────────────────────────────────────────────────────────────────
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return published;
    }
}

public sealed class KycOutboxPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    [FromKeyedServices(KycOutboxPublisherHostedService.HeartbeatKey)] LoopHeartbeat heartbeat,
    ILogger<KycOutboxPublisherHostedService> logger) : BackgroundService
{
    public const string HeartbeatKey = "kyc-outbox-relay";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<KycOutboxPublisher>().PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "KYC outbox publisher cycle failed."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}