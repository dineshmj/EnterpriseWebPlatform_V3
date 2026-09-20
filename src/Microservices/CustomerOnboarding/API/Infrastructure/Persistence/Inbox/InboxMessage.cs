namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;

public sealed class InboxMessage
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public string Consumer { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}