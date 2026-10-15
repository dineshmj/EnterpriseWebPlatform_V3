using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace EnterpriseWebPlatform.Common.Observability;

// ┌─ :: ARCHITECTURE :: OpenTelemetry: traces, metrics and logs with one trace ID ─────────────────
// │ Why : One business transaction crosses BFFs, APIs, the Outbox and Kafka; without a shared trace
// │       ID nobody can follow it or find the log lines of one failing request.
// │ How : Every .NET component gets the same OpenTelemetry set-up: W3C trace context, ASP.NET Core
// │       / HttpClient / EF Core spans, runtime and HTTP metrics, OTLP export when an endpoint is
// │       configured; logs carry the TraceId.
// │ Ref : Features 1.7.1, 1.7.3 | OpenTelemetry | W3C Trace Context |
// │       OWASP A09 Security Logging and Monitoring Failures
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class ObservabilityExtensions
{
    /// <summary>
    /// The three signals of an EWP host - traces, logs and metrics - set up the same way
    /// everywhere, each tagged with the host's own <paramref name="serviceName"/>:
    /// <list type="bullet">
    /// <item><b>Traces</b> (OpenTelemetry): outgoing HttpClient calls and the Kafka publish /
    /// process spans; the W3C trace context travels with every HTTP call and Kafka message.
    /// Hosts add their own instrumentation through <paramref name="configureTracing"/>
    /// (ASP.NET Core for web hosts, Npgsql for hosts with a database).</item>
    /// <item><b>Logs</b> (Serilog, <see cref="EwpLogging"/>): structured, every entry with the
    /// service name and the TraceId / SpanId, so a log line leads to its trace. Readable
    /// text in Development, JSON elsewhere.</item>
    /// <item><b>Metrics</b> (OpenTelemetry): HTTP, Kestrel, rate limiting, authentication and
    /// authorization, HttpClient, resilience (Polly), Npgsql and the .NET runtime, plus EWP's
    /// own: Kafka messages published / consumed by outcome, and every health check's status
    /// and numbers (Outbox backlog, sagas, ...). Scraped by Prometheus at <c>/metrics</c>
    /// (<see cref="MetricsEndpoints"/>).</item>
    /// </list>
    /// All three are also exported over OTLP (Grafana / Observe / Jaeger / the Aspire
    /// dashboard, through a collector) when OTEL_EXPORTER_OTLP_ENDPOINT or
    /// OpenTelemetry:OtlpEndpoint is set. Without it, nothing leaves the process except
    /// the console log and the scrape endpoint.
    /// </summary>
    public static IHostApplicationBuilder AddEwpObservability(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        // A friendly console window title for every service that uses this helper (local runs).
        ConsoleTitle.Set(ConsoleTitle.FromServiceName(serviceName));

        var otlpEndpoint =
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
            builder.Configuration["OpenTelemetry:OtlpEndpoint"];

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, logger) =>
            EwpLogging.Configure(logger, builder.Configuration, builder.Environment, serviceName, otlpEndpoint));

        // Each health check's status and numbers become metrics (re-evaluated every 30 s).
        builder.Services.AddSingleton<IHealthCheckPublisher, HealthMetricsPublisher>();
        builder.Services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(5);
            options.Period = TimeSpan.FromSeconds(30);
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(MessagingTelemetry.SourceName)
                    .AddHttpClientInstrumentation();

                configureTracing?.Invoke(tracing);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(BuiltInMeters)
                    .AddMeter(MessagingTelemetry.SourceName, HealthMetricsPublisher.MeterName)
                    .AddPrometheusExporter();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
            });

        return builder;
    }

    /// <summary>
    /// Meters that .NET, ASP.NET Core and the libraries already publish; no instrumentation
    /// package is needed. A meter a host does not use simply reports nothing.
    /// </summary>
    private static readonly string[] BuiltInMeters =
    [
        "Microsoft.AspNetCore.Hosting",           // http.server.request.duration, active requests
        "Microsoft.AspNetCore.Server.Kestrel",    // connections, TLS handshakes
        "Microsoft.AspNetCore.Http.Connections",  // SignalR connections
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",       // unhandled exceptions
        "Microsoft.AspNetCore.RateLimiting",      // rate-limited (429) requests
        "Microsoft.AspNetCore.Authentication",    // sign-ins, challenges, forbids
        "Microsoft.AspNetCore.Authorization",     // authorization decisions
        "System.Net.Http",                        // outgoing calls
        "System.Runtime",                         // GC, threads, CPU, memory
        "Polly",                                  // retries, timeouts, circuit breakers
        "Npgsql",                                 // database commands and connections
        "Duende.IdentityServer",
        "Duende.Bff"
    ];
}