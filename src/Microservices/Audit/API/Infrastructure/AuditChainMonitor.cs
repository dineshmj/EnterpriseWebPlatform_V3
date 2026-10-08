using Microsoft.Extensions.Diagnostics.HealthChecks;

using EnterpriseWebPlatform.Audit.Api.Application;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

/// <summary>The latest verification of the chain, shared by the monitor and the health check.</summary>
public sealed class AuditChainStatus
{
    public AuditChainVerification? Latest { get; set; }
}

/// <summary>
/// Re-verifies the whole chain on a schedule ("Audit:VerifyIntervalMinutes"), so tampering is
/// noticed without anyone asking: readiness turns Degraded and the metrics show where the
/// chain broke (ewp_health_value{check="audit-chain",key="brokenAtSequence"}) - an alert can
/// fire on it.
/// </summary>
public sealed class AuditChainMonitor(
    IServiceScopeFactory scopeFactory,
    AuditChainStatus status,
    IConfiguration configuration,
    ILogger<AuditChainMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Audit:VerifyIntervalMinutes", 10)));
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<AuditChainVerifier>().VerifyAsync(stoppingToken);
                status.Latest = result;

                if (result.Intact)
                    logger.LogInformation("Audit chain intact: {Entries} entries verified.", result.EntriesChecked);
                else
                    logger.LogCritical("AUDIT CHAIN BROKEN at entry {Sequence}: {Problem}.", result.BrokenAtSequence, result.Problem);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Audit chain verification could not run."); }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

/// <summary>Readiness: Degraded when the chain is broken (the service still records, and must, so it is not taken out of service).</summary>
public sealed class AuditChainHealthCheck(AuditChainStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (status.Latest is not { } latest)
            return Task.FromResult(HealthCheckResult.Healthy("Audit chain not verified yet."));

        var data = new Dictionary<string, object>
        {
            ["entriesVerified"] = latest.EntriesChecked,
            ["brokenAtSequence"] = latest.BrokenAtSequence ?? 0L
        };
        return Task.FromResult(latest.Intact
            ? HealthCheckResult.Healthy($"Audit chain intact ({latest.EntriesChecked} entries, verified {latest.VerifiedAt:u}).", data)
            : HealthCheckResult.Degraded($"Audit chain broken at entry {latest.BrokenAtSequence}: {latest.Problem}.", data: data));
    }
}