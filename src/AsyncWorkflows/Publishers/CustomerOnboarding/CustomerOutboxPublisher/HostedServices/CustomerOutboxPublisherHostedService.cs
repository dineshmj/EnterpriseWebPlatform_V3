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

    // Blanking expired payloads is housekeeping: once an hour (and at start-up) is plenty.
    private static readonly TimeSpan RedactionInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _nextRedaction = DateTimeOffset.MinValue;

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

                if (DateTimeOffset.UtcNow >= _nextRedaction)
                {
                    _nextRedaction = DateTimeOffset.UtcNow + RedactionInterval;
                    var redacted = await publisher.RedactExpiredPayloadsAsync(stoppingToken);
                    if (redacted > 0)
                        logger.LogInformation("Blanked the payload of {Count} Outbox messages published more than {Days} days ago.", redacted, options.Value.PayloadRetentionDays);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ If one cycle fails (e.g. the database is down), log it and try again at the next poll
            // │ - the loop never dies; the heartbeat keeps liveness honest.
            // └──────────────────────────────────────────────────────────────────────────────────────
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in Customer Outbox Publisher.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        logger.LogInformation("Customer Outbox Publisher stopped.");
    }
}