namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;

public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    private InboxMessage(
        Guid id,
        Guid messageId,
        string consumer,
        DateTimeOffset receivedAt)
    {
        Id = id;
        MessageId = messageId;
        Consumer = consumer;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string Consumer { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static InboxMessage Create(
        Guid messageId,
        string consumer,
        DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        return new InboxMessage(
            Guid.NewGuid(),
            messageId,
            consumer,
            receivedAt);
    }

    public void MarkProcessed(DateTimeOffset processedAt)
    {
        ProcessedAt = processedAt;
    }
}
