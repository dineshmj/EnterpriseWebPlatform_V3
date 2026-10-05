using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.CoreBanking;

/// <summary>
/// Background account opening: every few seconds, opens the approved accounts that are
/// due, one at a time, until none is left or core banking stops answering. Several API
/// instances share the work safely (SKIP LOCKED in the repository).
/// </summary>
public sealed class AccountOpeningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CoreBankingOptions> options,
    [FromKeyedServices(AccountOpeningWorker.HeartbeatKey)] LoopHeartbeat heartbeat,
    ILogger<AccountOpeningWorker> logger) : BackgroundService
{
    public const string HeartbeatKey = "accounts-opening";

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
                    var outcome = await scope.ServiceProvider.GetRequiredService<OpenDueAccountCommandHandler>().HandleAsync(stoppingToken);

                    // Stop this round when nothing is due, or core banking is failing:
                    // the remaining applications keep their own retry times.
                    if (outcome is OpeningRunOutcome.NothingDue or OpeningRunOutcome.CoreBankingUnavailable)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Account opening cycle failed."); }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

/// <summary>
/// Readiness signal: Degraded (never Unhealthy) while approved accounts wait for core
/// banking longer than expected - typically it is down or the circuit breaker is open.
/// The API itself is fine, so this must not take it out of service.
/// </summary>
public sealed class OpeningBacklogHealthCheck(AccountsDbContext db) : IHealthCheck
{
    private static readonly TimeSpan Overdue = TimeSpan.FromMinutes(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - Overdue;
        var waiting = await db.AccountApplications.AsNoTracking()
            .Where(x => x.Status == AccountApplicationStatus.Opening)
            .Select(x => new { x.DecisionAt, x.OpeningAttempts, x.LastOpeningError })
            .ToListAsync(cancellationToken);

        var overdue = waiting.Where(x => x.DecisionAt < cutoff).ToList();
        var data = new Dictionary<string, object> { ["waiting"] = waiting.Count, ["overdue"] = overdue.Count };

        if (overdue.Count == 0)
            return HealthCheckResult.Healthy($"{waiting.Count} account(s) being opened.", data);

        var lastError = overdue.Select(x => x.LastOpeningError).FirstOrDefault(e => e is not null);
        return HealthCheckResult.Degraded(
            $"{overdue.Count} approved account(s) have waited more than {Overdue.TotalMinutes:F0} min for core banking. Last error: {lastError ?? "none yet"}",
            data: data);
    }
}