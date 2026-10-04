using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EnterpriseWebPlatform.Common.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// OpenTelemetry distributed tracing for an EWP host:
    ///  - every service reports under its own <paramref name="serviceName"/>;
    ///  - outgoing HttpClient calls and the Kafka publish / process spans are traced,
    ///    and the W3C trace context travels with every HTTP call and Kafka message;
    ///  - log entries carry the TraceId / SpanId, so logs and traces line up;
    ///  - spans are exported over OTLP (Jaeger, the .NET Aspire dashboard, Azure
    ///    Monitor, AWS X-Ray via the collector, ...) only when an endpoint is
    ///    configured: OTEL_EXPORTER_OTLP_ENDPOINT or OpenTelemetry:OtlpEndpoint.
    ///    Without one, trace context is still created and propagated.
    /// Hosts add their own instrumentation through <paramref name="configureTracing"/>
    /// (ASP.NET Core for web hosts, Npgsql for hosts with a database).
    /// </summary>
    public static IHostApplicationBuilder AddEwpObservability(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

        var otlpEndpoint =
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
            builder.Configuration["OpenTelemetry:OtlpEndpoint"];

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
            });

        return builder;
    }
}
