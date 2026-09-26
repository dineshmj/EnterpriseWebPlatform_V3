namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;

public interface IKafkaConsumer : IDisposable
{
    Task ConsumeAsync(
        string topic,
        string groupId,
        Func<string, string, CancellationToken, Task> messageHandler,
        CancellationToken cancellationToken);
}
