using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// Kubernetes-style health endpoints, the same on every EWP component:
///  - GET /health/live  : liveness  - is the process working? (failing → restart it)
///  - GET /health/ready : readiness - can it do its job now? (failing → no traffic / wait)
/// Checks are selected by tag ("live" / "ready"). Healthy and Degraded answer 200,
/// Unhealthy answers 503. The body lists each check's status and description only:
/// no exception details, connection strings or stack traces.
/// </summary>
public static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>Maps /health/live and /health/ready on a web host (anonymous, no CSRF, not cached).</summary>
    public static IEndpointRouteBuilder MapEwpHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", Options(LiveTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", Options(ReadyTag)).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// For a worker (generic host, no web server): serves the same two endpoints - and
    /// the Prometheus scrape endpoint /metrics (<see cref="MetricsEndpoints"/>) - from a
    /// minimal Kestrel listener on Health:Urls (e.g. http://localhost:5101; in a container
    /// http://+:8080), so the orchestrator can probe and scrape the worker like any other
    /// service. No listener is started when Health:Urls is empty.
    /// </summary>
    public static IHostApplicationBuilder AddWorkerHealthEndpoints(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks();
        builder.Services.AddHostedService<WorkerHealthEndpointHostedService>();
        return builder;
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = WriteResponseAsync
    };

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data.Count > 0 ? e.Value.Data : null
            })
        }));
    }

    private sealed class WorkerHealthEndpointHostedService(
        HealthCheckService healthChecks,
        IConfiguration configuration,
        IHostEnvironment environment,
        IServiceProvider services,
        ILogger<WorkerHealthEndpointHostedService> logger) : IHostedService
    {
        private WebApplication? _app;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var urls = configuration["Health:Urls"];
            if (string.IsNullOrWhiteSpace(urls))
            {
                logger.LogInformation("Health endpoints disabled (Health:Urls is not set).");
                return;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(urls);
            builder.Logging.ClearProviders();
            // The worker's own checks, evaluated by the worker's HealthCheckService.
            builder.Services.AddSingleton(healthChecks);

            _app = builder.Build();
            _app.MapGet("/health/live", (HttpContext http) => RespondAsync(http, LiveTag));
            _app.MapGet("/health/ready", (HttpContext http) => RespondAsync(http, ReadyTag));

            // The worker's own metrics (its MeterProvider), scraped through this listener.
            var metrics = MetricsEndpoints.IsEnabled(configuration, environment) ? services.GetService<MeterProvider>() : null;
            if (metrics is not null)
                _app.MapPrometheusScrapingEndpoint(MetricsEndpoints.Path, metrics, configureBranchedPipeline: null, optionsName: null);

            await _app.StartAsync(cancellationToken);
            logger.LogInformation("Health endpoints listening on {Urls} (/health/live, /health/ready{Metrics}).",
                urls, metrics is null ? "" : ", " + MetricsEndpoints.Path);
        }

        private async Task RespondAsync(HttpContext http, string tag)
        {
            var report = await healthChecks.CheckHealthAsync(check => check.Tags.Contains(tag), http.RequestAborted);
            http.Response.StatusCode = report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            await WriteResponseAsync(http, report);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_app is not null)
                await _app.StopAsync(cancellationToken);
        }
    }
}