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
        // Only event types with an explicit Kafka contract are selected.
        // An unknown/unhandled outbox row must never occupy the batch and
        // prevent newer, publishable events from being processed.
        var messages = await dbContext.OutboxMessages
            .Where(x => x.PublishedAt == null &&
                        (x.EventType == "CustomerCreated" ||
                         x.EventType == "OnboardingApplicationSubmitted" ||
                         x.EventType == "OnboardingApplicationStatusChanged"))
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var topic = message.EventType switch
            {
                "CustomerCreated" => KafkaTopicNames.CustomerCreated,
                "OnboardingApplicationSubmitted" =>
                    KafkaTopicNames.OnboardingApplicationSubmitted,
                "OnboardingApplicationStatusChanged" =>
                    KafkaTopicNames.OnboardingApplicationStatusChanged,
                _ => null
            };

            // The query above and this routing map intentionally form a
            // defensive double-check. Unknown event types remain visible in
            // the database for investigation, but can never block known
            // publishable events.
            if (topic is null)
            {
                logger.LogError(
                    "Outbox message {MessageId} has unsupported event type {EventType}. " +
                    "The message will remain unpublished and will not block other outbox messages.",
                    message.Id,
                    message.EventType);

                continue;
            }

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
                    cancellationToken);

                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastAttemptAt = DateTimeOffset.UtcNow;
                message.AttemptCount++;

                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Published Outbox message {MessageId} EventType={EventType} " +
                    "for aggregate {AggregateId} to topic {Topic}. InitiatedByUserId={InitiatedByUserId}.",
                    message.Id,
                    message.EventType,
                    message.AggregateId,
                    topic,
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
                    "Failed to publish Outbox message {MessageId} EventType={EventType} " +
                    "for aggregate {AggregateId} to topic {Topic}.",
                    message.Id,
                    message.EventType,
                    message.AggregateId,
                    topic);
            }
        }
    }
}
