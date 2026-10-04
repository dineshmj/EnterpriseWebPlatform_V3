using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;

/// <summary>
/// Background screening: every few seconds, screens the cases that are due, one at a
/// time, until none is left or the provider stops answering. Several API instances
/// share the work safely (SKIP LOCKED in the repository).
/// </summary>
public sealed class ScreeningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ScreeningProviderOptions> options,
    [FromKeyedServices(ScreeningWorker.HeartbeatKey)] LoopHeartbeat heartbeat,
    ILogger<ScreeningWorker> logger) : BackgroundService
{
    public const string HeartbeatKey = "compliance-screening";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    using var scope = scopeFactory.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<ScreenDueCaseCommandHandler>().HandleAsync(stoppingToken);

                    // Stop this round when nothing is due, or the provider is failing:
                    // the remaining cases keep their own retry times.
                    if (outcome != ScreeningRunOutcome.Screened)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Screening cycle failed."); }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

/// <summary>
/// Readiness signal: Degraded (never Unhealthy) while cases wait for screening longer
/// than expected - typically the provider is down or the circuit breaker is open. The
/// API itself is fine, so this must not take it out of service.
/// </summary>
public sealed class ScreeningBacklogHealthCheck(ComplianceDbContext db) : IHealthCheck
{
    private static readonly TimeSpan Overdue = TimeSpan.FromMinutes(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - Overdue;
        var waiting = await db.ComplianceCases.AsNoTracking()
            .Where(x => x.Status == ComplianceCaseStatus.Screening)
            .Select(x => new { x.CreatedAt, x.ScreeningAttempts, x.LastScreeningError })
            .ToListAsync(cancellationToken);

        var overdue = waiting.Where(x => x.CreatedAt < cutoff).ToList();
        var data = new Dictionary<string, object> { ["waiting"] = waiting.Count, ["overdue"] = overdue.Count };

        if (overdue.Count == 0)
            return HealthCheckResult.Healthy($"{waiting.Count} case(s) waiting for screening.", data);

        var lastError = overdue.Select(x => x.LastScreeningError).FirstOrDefault(e => e is not null);
        return HealthCheckResult.Degraded(
            $"{overdue.Count} case(s) have waited more than {Overdue.TotalMinutes:F0} min for screening. Last provider error: {lastError ?? "none yet"}",
            data: data);
    }
}