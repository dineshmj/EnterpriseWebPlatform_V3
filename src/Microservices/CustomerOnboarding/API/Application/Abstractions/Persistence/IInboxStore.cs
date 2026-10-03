namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

/// <summary>
/// Inbox (idempotent consumer). A recorded message is saved in the SAME
/// transaction as the business change it caused, so a redelivered message is
/// recognised and ignored - "processed exactly once" on top of at-least-once delivery.
/// </summary>
public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    /// <summary>Records the message; persisted by the next unit-of-work save.</summary>
    void RecordProcessed(Guid messageId, string consumer);
}
