namespace EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

/// <summary>
/// Idempotent consumer (Inbox): a message is recorded in the same transaction as the
/// change it caused, so a redelivered message is recognised and changes nothing.
/// </summary>
public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    void RecordProcessed(Guid messageId, string consumer, DateTimeOffset now);
}