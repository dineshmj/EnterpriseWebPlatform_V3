namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;

public interface ICustomerOutboxPublisher
{
    Task PublishPendingAsync(CancellationToken cancellationToken);

    /// <summary>Blanks the payload of messages published longer ago than the retention period; returns how many.</summary>
    Task<int> RedactExpiredPayloadsAsync(CancellationToken cancellationToken);
}