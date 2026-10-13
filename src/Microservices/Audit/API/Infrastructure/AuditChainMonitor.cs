using Microsoft.Extensions.Diagnostics.HealthChecks;

using EnterpriseWebPlatform.Audit.Api.Application;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

/// <summary>The latest verification of the chain and of its anchors, shared by the monitor and the health check.</summary>
public sealed class AuditChainStatus
{
    public AuditChainVerification? Latest { get; set; }

    /// <summary>How often the monitor runs ("Audit:VerifyIntervalMinutes").</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The newest entry anchored outside the database (the latest checkpoint's sequence).</summary>
    public long AnchoredSequence { get; set; }

    /// <summary>Checkpoint files now, and the most ever seen since start-up: fewer now means files went missing.</summary>
    public int Checkpoints { get; set; }
    public int MostCheckpointsSeen { get; set; }
}

/// <summary>
/// Re-verifies the whole chain on a schedule ("Audit:VerifyIntervalMinutes"), so tampering is
/// noticed without anyone asking: readiness turns Degraded and the metrics show where the
/// chain broke (ewp_health_value{check="audit-chain",key="brokenAtSequence"}) - an alert can
/// fire on it. After every verification that passes, the chain's newest entry is anchored: a
/// signed checkpoint is written OUTSIDE the database. A broken chain is never anchored.
/// </summary>
public sealed class AuditChainMonitor(
    IServiceScopeFactory scopeFactory,
    AuditChainStatus status,
    AuditAnchorStore anchors,
    AuditCheckpointSigner signer,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<AuditChainMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Audit:VerifyIntervalMinutes", 10)));
        status.Interval = interval;
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<AuditChainVerifier>().VerifyAsync(stoppingToken);
                status.Latest = result;

                if (result.Intact)
                {
                    logger.LogInformation("Audit chain intact: {Entries} entries verified, {Checkpoints} checkpoint(s) matched.",
                        result.EntriesChecked, result.CheckpointsChecked);

                    if (result.LastEntryHash is { } lastHash && result.LastSequence > await anchors.LatestSequenceAsync(stoppingToken))
                    {
                        await anchors.AppendAsync(signer.Sign(result.LastSequence, lastHash, time.GetUtcNow()), stoppingToken);
                        logger.LogInformation("Audit chain anchored outside the database at entry {Sequence}.", result.LastSequence);
                    }
                }
                else
                    logger.LogCritical("AUDIT CHAIN BROKEN at entry {Sequence}: {Problem}.", result.BrokenAtSequence, result.Problem);

                var checkpoints = await anchors.ReadAllAsync(stoppingToken);
                status.AnchoredSequence = checkpoints.Select(c => c.Sequence).DefaultIfEmpty(0).Max();
                status.Checkpoints = checkpoints.Count;
                status.MostCheckpointsSeen = Math.Max(status.MostCheckpointsSeen, checkpoints.Count);
                if (status.Checkpoints < status.MostCheckpointsSeen)
                    logger.LogCritical("AUDIT CHECKPOINTS MISSING: {Missing} checkpoint file(s) disappeared since start-up.", status.MostCheckpointsSeen - status.Checkpoints);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Audit chain verification could not run."); }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

/// <summary>
/// Readiness: Degraded (the service still records, and must, so it is not taken out of service) when
///  - the chain is broken;
///  - the verification is OVERDUE (more than two intervals and a minute since the last one: the
///    monitor stopped, or the verification keeps failing);
///  - the newest verified entry is NOT ANCHORED (writing the checkpoint failed);
///  - checkpoint files went MISSING since start-up.
/// The metrics carry the same figures, so an alert can fire on each.
/// </summary>
public sealed class AuditChainHealthCheck(AuditChainStatus status, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (status.Latest is not { } latest)
            return Task.FromResult(HealthCheckResult.Healthy("Audit chain not verified yet."));

        var age = time.GetUtcNow() - latest.VerifiedAt;
        var data = new Dictionary<string, object>
        {
            ["entriesVerified"] = latest.EntriesChecked,
            ["brokenAtSequence"] = latest.BrokenAtSequence ?? 0L,
            ["minutesSinceVerification"] = Math.Round(age.TotalMinutes, 1),
            ["unanchoredEntries"] = Math.Max(0, latest.LastSequence - status.AnchoredSequence),
            ["missingCheckpoints"] = status.MostCheckpointsSeen - status.Checkpoints
        };

        var problems = new List<string>();
        if (!latest.Intact)
            problems.Add($"Audit chain broken at entry {latest.BrokenAtSequence}: {latest.Problem}");
        if (age > status.Interval * 2 + TimeSpan.FromMinutes(1))
            problems.Add($"Audit chain verification overdue: last one {age.TotalMinutes:F0} min ago");
        if (latest.Intact && latest.LastSequence > status.AnchoredSequence)
            problems.Add($"Audit chain not anchored beyond entry {status.AnchoredSequence} (newest verified entry {latest.LastSequence})");
        if (status.Checkpoints < status.MostCheckpointsSeen)
            problems.Add($"{status.MostCheckpointsSeen - status.Checkpoints} audit checkpoint file(s) missing since start-up");

        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy($"Audit chain intact and anchored ({latest.EntriesChecked} entries, verified {latest.VerifiedAt:u}).", data)
            : HealthCheckResult.Degraded(string.Join("; ", problems) + ".", data: data));
    }
}