using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

/// <summary>
/// Live state of the consume loop, written by <see cref="KafkaSubscriberHostedService{TProcessor, TSettings}"/>
/// and read by the health checks (thread-safe: simple atomic fields).
/// </summary>
public sealed class SubscriberHealth
{
    private long _heartbeatTicks;
    private volatile bool _started;
    private volatile bool _joinedGroup;
    private volatile string? _groupId;
    private volatile string? _retrying;
    private volatile string? _lastKafkaError;
    private long _processed;
    private long _deadLettered;

    public void Started(string groupId) { _groupId = groupId; _started = true; Heartbeat(); }

    public void Heartbeat() => Interlocked.Exchange(ref _heartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);

    public void PartitionsAssigned(int count) { _joinedGroup = true; _lastKafkaError = null; }

    public void KafkaError(string reason) => _lastKafkaError = reason;

    public void Retrying(string position, int attempt, string reason) =>
        _retrying = $"retrying {position} (attempt {attempt}): {reason}";

    public void Processed(bool deadLettered)
    {
        _retrying = null;
        Interlocked.Increment(ref _processed);
        if (deadLettered)
            Interlocked.Increment(ref _deadLettered);
    }

    internal bool IsStarted => _started;
    internal bool HasJoinedGroup => _joinedGroup;
    internal string? GroupId => _groupId;
    internal string? RetryingDescription => _retrying;
    internal string? LastKafkaError => _lastKafkaError;
    internal TimeSpan SinceHeartbeat => DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _heartbeatTicks), TimeSpan.Zero);

    internal IReadOnlyDictionary<string, object> Data() => new Dictionary<string, object>
    {
        ["groupId"] = _groupId ?? "-",
        ["processed"] = Interlocked.Read(ref _processed),
        ["deadLettered"] = Interlocked.Read(ref _deadLettered)
    };
}

/// <summary>
/// Liveness: the consume loop is still going round (it polls every second, and a
/// transient retry waits at most a minute). A loop that has not ticked for three
/// minutes is stuck, and the orchestrator should restart the instance.
/// </summary>
public sealed class SubscriberLivenessCheck(SubscriberHealth health) : IHealthCheck
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(3);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!health.IsStarted)
            return Task.FromResult(HealthCheckResult.Healthy("Starting."));

        var since = health.SinceHeartbeat;
        return Task.FromResult(since > StuckAfter
            ? HealthCheckResult.Unhealthy($"Consume loop has not run for {since.TotalSeconds:F0} s.", data: health.Data())
            : HealthCheckResult.Healthy("Consume loop running.", health.Data()));
    }
}

/// <summary>
/// Readiness: the consumer has joined its group (which also proves that Kafka accepted
/// its credentials and ACLs). An instance with no partitions (a hot standby) is ready.
/// While a message is being retried in place the result is Degraded, not Unhealthy:
/// the dependency is down, not this instance, so restarting it would not help.
/// </summary>
public sealed class SubscriberReadinessCheck(SubscriberHealth health) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!health.HasJoinedGroup)
        {
            var reason = health.LastKafkaError is { } error
                ? $"Not connected to consumer group {health.GroupId}: {error}"
                : $"Joining consumer group {health.GroupId}.";
            return Task.FromResult(HealthCheckResult.Unhealthy(reason, data: health.Data()));
        }

        return Task.FromResult(health.RetryingDescription is { } retrying
            ? HealthCheckResult.Degraded(retrying, data: health.Data())
            : HealthCheckResult.Healthy("Consuming.", health.Data()));
    }
}
