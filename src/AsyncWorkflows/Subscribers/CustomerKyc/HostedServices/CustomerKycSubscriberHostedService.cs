using System.Text.Json;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Contracts.Customer.CustomerCreated;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Consumer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.HostedServices;

public sealed class CustomerKycSubscriberHostedService : BackgroundService
{
    private readonly IKafkaConsumer _consumer;
    private readonly CustomerKycSubscriberOptions _options;
    private readonly ILogger<CustomerKycSubscriberHostedService> _logger;

    public CustomerKycSubscriberHostedService(
        IKafkaConsumer consumer,
        IOptions<CustomerKycSubscriberOptions> options,
        ILogger<CustomerKycSubscriberHostedService> logger)
    {
        _consumer = consumer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Customer KYC Subscriber started. Topic={Topic}, GroupId={GroupId}",
            _options.Topic,
            _options.GroupId);

        await _consumer.ConsumeAsync(
            _options.Topic,
            _options.GroupId,
            HandleMessageAsync,
            stoppingToken);
    }

    private Task HandleMessageAsync(
        string key,
        string payload,
        CancellationToken cancellationToken)
    {
        var integrationEvent =
            JsonSerializer.Deserialize<CustomerCreatedIntegrationEvent>(payload);

        if (integrationEvent is null)
        {
            throw new InvalidOperationException(
                "Received customer.created message could not be deserialized.");
        }

        _logger.LogInformation(
            "Received CustomerCreatedIntegrationEvent. MessageId={MessageId}, CustomerNumber={CustomerNumber}, Email={Email}, KafkaKey={KafkaKey}",
            integrationEvent.MessageId,
            integrationEvent.CustomerNumber,
            integrationEvent.Email,
            key);

        _logger.LogInformation(
            "Customer KYC processing placeholder. The KYC API will be invoked here once the Customer KYC bounded context is implemented.");

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Customer KYC Subscriber is stopping.");
        return base.StopAsync(cancellationToken);
    }
}
