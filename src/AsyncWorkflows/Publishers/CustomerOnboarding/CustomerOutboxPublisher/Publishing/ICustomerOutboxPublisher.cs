namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;

public interface ICustomerOutboxPublisher
{
    Task PublishPendingAsync(CancellationToken cancellationToken);
}