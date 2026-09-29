using Microsoft.EntityFrameworkCore;

using Confluent.Kafka;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycOutboxPublisher(KycDbContext db, IConfiguration config, ILogger<KycOutboxPublisher> logger)
{
    public async Task PublishPendingAsync(CancellationToken ct)
    {
        var servers = config["Kafka:BootstrapServers"] ?? "localhost:9092";
        var topic = config["Kafka:KycCaseCreatedTopic"] ?? "kyc.case.created";
        var messages = await db.OutboxMessages.Where(x => x.PublishedAt == null).OrderBy(x => x.OccurredAt).Take(50).ToListAsync(ct);
        if (messages.Count == 0) return;

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = servers }).Build();
        foreach (var message in messages)
        {
            try
            {
                await producer.ProduceAsync(topic, new Message<string, string> { Key = message.AggregateId, Value = message.Payload }, ct);
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastAttemptAt = DateTimeOffset.UtcNow;
                message.AttemptCount++;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Published KYC outbox message {MessageId} to {Topic}.", message.Id, topic);
            }
            catch (Exception ex)
            {
                message.AttemptCount++;
                message.LastAttemptAt = DateTimeOffset.UtcNow;
                message.LastError = ex.Message;
                await db.SaveChangesAsync(ct);
                logger.LogError(ex, "Failed to publish KYC outbox message {MessageId}.", message.Id);
            }
        }
    }
}

public sealed class KycOutboxPublisherHostedService(IServiceScopeFactory scopeFactory, ILogger<KycOutboxPublisherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
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