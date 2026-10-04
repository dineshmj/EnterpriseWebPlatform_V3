using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>Unpublished Outbox rows: still being retried (Pending) or given up on (Parked).</summary>
public sealed record OutboxBacklog(long Parked, long Pending, double? OldestPendingSeconds);

/// <summary>
/// Readiness signal for an Outbox relay. Degraded (never Unhealthy) when messages are
/// parked or have waited too long: the relay itself is fine, but an operator should
/// look (Kafka down, a topic missing, an ACL denying the write). Restarting the
/// instance would not help, so this must not fail liveness.
/// </summary>
public sealed class OutboxBacklogHealthCheck(Func<CancellationToken, Task<OutboxBacklog>> query, TimeSpan maxPendingAge)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await query(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["parked"] = backlog.Parked,
            ["pending"] = backlog.Pending,
            ["oldestPendingSeconds"] = Math.Round(backlog.OldestPendingSeconds ?? 0)
        };

        if (backlog.Parked > 0)
            return HealthCheckResult.Degraded($"{backlog.Parked} Outbox message(s) parked after repeated failures.", data: data);

        if (backlog.OldestPendingSeconds is { } oldest && oldest > maxPendingAge.TotalSeconds)
            return HealthCheckResult.Degraded($"Oldest unpublished Outbox message has waited {oldest:F0} s.", data: data);

        return HealthCheckResult.Healthy($"{backlog.Pending} message(s) pending.", data);
    }
}
