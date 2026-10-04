using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.HostedServices;

public sealed class CustomerOutboxPublisherHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<CustomerOutboxPublisherOptions> options,
    LoopHeartbeat heartbeat,
    ILogger<CustomerOutboxPublisherHostedService> logger)
    : BackgroundService
{
    private readonly TimeSpan _pollInterval =
        TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Customer Outbox Publisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                using var scope = scopeFactory.CreateScope();

                var publisher = scope.ServiceProvider
                    .GetRequiredService<ICustomerOutboxPublisher>();

                await publisher.PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in Customer Outbox Publisher.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        logger.LogInformation("Customer Outbox Publisher stopped.");
    }
}