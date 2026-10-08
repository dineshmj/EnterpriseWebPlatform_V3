using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Metrics;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// The Prometheus scrape endpoint, <c>GET /metrics</c>, the same on every host (web hosts
/// map it next to the health endpoints; workers serve it from their health listener).
/// It is anonymous, like the health probes, and is meant for the cluster's own scraper:
/// ingress must not route it to the internet. Outside Development it is off unless
/// "Observability:Metrics:Enabled" is true, so a deployment exposes it only on purpose.
/// </summary>
public static class MetricsEndpoints
{
    public const string Path = "/metrics";
    public const string EnabledKey = "Observability:Metrics:Enabled";

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>(EnabledKey) ?? environment.IsDevelopment();

    public static IEndpointRouteBuilder MapEwpMetricsEndpoint(this WebApplication app)
    {
        if (IsEnabled(app.Configuration, app.Environment))
            app.MapPrometheusScrapingEndpoint(Path).AllowAnonymous();
        return app;
    }
}

/// <summary>
/// Turns the host's health checks into metrics, so a dashboard and an alert see what the
/// readiness probe sees - without a separate query for each number:
///   ewp_health_status{check}     2 healthy, 1 degraded, 0 unhealthy
///   ewp_health_value{check,key}  every number a check reports (e.g. the Outbox backlog,
///                                running / overdue / stuck payment sagas)
/// ASP.NET Core re-runs the checks every 30 seconds and hands the report to this publisher.
/// </summary>
public sealed class HealthMetricsPublisher : IHealthCheckPublisher
{
    public const string MeterName = "EnterpriseWebPlatform.Health";

    private volatile HealthReport? _last;

    public HealthMetricsPublisher(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("ewp.health.status", ObserveStatus,
            description: "Health check status: 2 healthy, 1 degraded, 0 unhealthy.");
        meter.CreateObservableGauge("ewp.health.value", ObserveValues,
            description: "Numbers reported by health checks (Outbox backlog, sagas, ...).");
    }

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        _last = report;
        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<int>> ObserveStatus() =>
        _last?.Entries.Select(e => new Measurement<int>(
            e.Value.Status switch { HealthStatus.Healthy => 2, HealthStatus.Degraded => 1, _ => 0 },
            new KeyValuePair<string, object?>("check", e.Key))) ?? [];

    private IEnumerable<Measurement<double>> ObserveValues()
    {
        if (_last is not { } report)
            yield break;

        foreach (var (check, entry) in report.Entries)
        {
            foreach (var (key, value) in entry.Data)
            {
                double? number = value switch
                {
                    int i => i, long l => l, double d => d, float f => f, decimal m => (double)m,
                    TimeSpan t => t.TotalSeconds,
                    _ => null
                };
                if (number is { } n)
                    yield return new Measurement<double>(n,
                        new KeyValuePair<string, object?>("check", check),
                        new KeyValuePair<string, object?>("key", key));
            }
        }
    }
}