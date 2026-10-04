namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

/// <summary>One consumed message, recorded by the consumer that processed it (Inbox).</summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string Consumer { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static InboxMessage Processed(Guid messageId, string consumer, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        return new InboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            Consumer = consumer,
            ReceivedAt = now,
            ProcessedAt = now
        };
    }
}