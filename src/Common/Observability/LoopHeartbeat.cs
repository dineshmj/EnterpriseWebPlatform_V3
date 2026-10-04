using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// Heartbeat of a background loop (e.g. an Outbox relay): the loop calls
/// <see cref="Beat"/> on every cycle, and liveness fails when it stops.
/// </summary>
public sealed class LoopHeartbeat
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public void Beat() => Interlocked.Exchange(ref _ticks, DateTimeOffset.UtcNow.UtcTicks);

    public TimeSpan SinceLastBeat =>
        DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>Unhealthy when the loop has not completed a cycle within <paramref name="stuckAfter"/>.</summary>
public sealed class LoopHeartbeatHealthCheck(LoopHeartbeat heartbeat, string loopName, TimeSpan stuckAfter) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var since = heartbeat.SinceLastBeat;
        return Task.FromResult(since > stuckAfter
            ? HealthCheckResult.Unhealthy($"{loopName} has not completed a cycle for {since.TotalSeconds:F0} s.")
            : HealthCheckResult.Healthy($"{loopName} running."));
    }
}
