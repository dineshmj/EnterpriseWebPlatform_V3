using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;

public sealed class CustomerOutboxPublisher(
    CustomerOutboxDbContext dbContext,
    IKafkaProducer kafkaProducer,
    IOptions<CustomerOutboxPublisherOptions> options,
    ILogger<CustomerOutboxPublisher> logger)
    : ICustomerOutboxPublisher
{
    private readonly CustomerOutboxPublisherOptions _options = options.Value;

    public async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        var messages = await dbContext.OutboxMessages
            .Where(x => x.PublishedAt == null)
            .OrderBy(x => x.OccurredAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            if (!string.Equals(message.EventType, "CustomerCreated", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Outbox message {MessageId} has unsupported event type {EventType}.",
                    message.Id,
                    message.EventType);
                continue;
            }

            try
            {
                if (message.InitiatedByUserId is null)
                {
                    logger.LogWarning(
                        "Outbox message {MessageId} has no InitiatedByUserId. The event will be published, but downstream user-targeted notifications cannot be routed to a human initiator.",
                        message.Id);
                }

                await kafkaProducer.ProduceAsync(
                    KafkaTopicNames.CustomerCreated,
                    message.AggregateId,
                    message.Payload,
                    cancellationToken);

                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastAttemptAt = DateTimeOffset.UtcNow;
                message.AttemptCount++;

                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Published Outbox message {MessageId} for aggregate {AggregateId} to topic {Topic}. InitiatedByUserId={InitiatedByUserId}.",
                    message.Id,
                    message.AggregateId,
                    KafkaTopicNames.CustomerCreated,
                    message.InitiatedByUserId);
            }
            catch (Exception ex)
            {
                message.AttemptCount++;
                message.LastAttemptAt = DateTimeOffset.UtcNow;
                message.LastError = ex.Message;

                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogError(
                    ex,
                    "Failed to publish Outbox message {MessageId} for aggregate {AggregateId}.",
                    message.Id,
                    message.AggregateId);
            }
        }
    }
}
