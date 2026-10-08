using EnterpriseWebPlatform.Audit.Api.Application;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

/// <summary>
/// The audit trail's Kafka consumer, running INSIDE the Audit API (no separate worker):
/// every business event topic, read with the API's own read-only Kafka user. Commands
/// (accounts.commands) and dead-letter topics are not facts and are not read.
/// The group starts at the earliest offset, so a new trail first records the history
/// still held by Kafka.
/// </summary>
public sealed class AuditTrailSubscriberOptions : ISubscriberSettings
{
    public const string SectionName = "AuditTrailSubscriber";

    /// <summary>Fixed in code: what the trail records is part of its contract.</summary>
    public IReadOnlyList<string> Topics { get; } =
    [
        KafkaTopicNames.CustomerCreated,
        KafkaTopicNames.OnboardingApplicationSubmitted,
        KafkaTopicNames.OnboardingApplicationStatusChanged,
        KafkaTopicNames.OnboardingApplicationRejected,
        KafkaTopicNames.KycCaseCreated,
        KafkaTopicNames.KycIdentityVerificationApproved,
        KafkaTopicNames.KycIdentityVerificationRejected,
        KafkaTopicNames.KycDocumentVerificationApproved,
        KafkaTopicNames.KycDocumentVerificationRejected,
        KafkaTopicNames.KycCaseApproved,
        KafkaTopicNames.KycCaseRejected,
        KafkaTopicNames.ComplianceCaseCreated,
        KafkaTopicNames.ComplianceCaseScreened,
        KafkaTopicNames.ComplianceCaseApproved,
        KafkaTopicNames.ComplianceCaseRejected,
        KafkaTopicNames.AccountApplicationCreated,
        KafkaTopicNames.AccountApplicationRejected,
        KafkaTopicNames.AccountOpened,
        KafkaTopicNames.AccountOpeningFailed,
        KafkaTopicNames.AccountsFundsReplies,
        KafkaTopicNames.PaymentEvents
    ];

    /// <summary>The Kafka ACLs grant the Audit API's user READ on this group only.</summary>
    public string GroupId { get; init; } = "audit.trail-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.AuditTrailSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;
}

/// <summary>
/// Records each consumed event in the trail. Classification for the shared consume loop:
///  - Processed   : appended, or already recorded (redelivery);
///  - Dead letter : not JSON at all (nothing meaningful can be recorded);
///  - Transient   : the database is unavailable - the exception propagates, and the
///                  message is retried in place, never skipped (a gap in an audit trail
///                  is exactly what must not happen).
/// </summary>
public sealed class AuditTrailProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<AuditTrailProcessor> logger) : IMessageProcessor
{
    public async Task<ProcessingOutcome> ProcessAsync(ConsumedMessage message, CancellationToken cancellationToken)
    {
        var entry = AuditEventMapper.Map(message.Topic, message.Partition, message.Offset, message.Value, time.GetUtcNow());
        if (entry is null)
            return ProcessingOutcome.ToDeadLetter("Not a JSON object: nothing can be recorded.");

        using var scope = scopeFactory.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AuditTrailAppender>().AppendAsync(entry, cancellationToken);

        logger.LogInformation("{Result} {EventType} {RecordRef} as entry {Sequence} (MessageId={MessageId}).",
            result, entry.EventType, entry.RecordRef, result == AppendResult.Appended ? entry.Sequence : null, entry.MessageId);
        return ProcessingOutcome.Processed;
    }
}