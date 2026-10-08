using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// Structured logging with Serilog, the same on every host:
///  - every entry carries the service name, the environment and the TraceId / SpanId;
///  - the console shows readable text in Development and one JSON object per line
///    elsewhere (a log shipper reads it as-is); "Observability:LogFormat" = "Text" or
///    "Json" overrides the default;
///  - entries also go over OTLP when an endpoint is configured;
///  - the levels come from the host's existing "Logging:LogLevel" section (Default and
///    per-category overrides), so appsettings keep working unchanged; an optional
///    "Serilog" section can refine them.
/// What is logged stays the application's choice: request logging records method, path
/// (never the query string), status, duration and who called - no bodies, headers or tokens.
/// </summary>
public static class EwpLogging
{
    public const string LogFormatKey = "Observability:LogFormat";

    private const string TextTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}  <{SourceContext}>{TraceSuffix}{NewLine}{Exception}";

    public static void Configure(
        LoggerConfiguration logger,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        string? otlpEndpoint)
    {
        ApplyLevels(logger, configuration.GetSection("Logging:LogLevel"));
        logger.ReadFrom.Configuration(configuration);

        logger
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .Enrich.WithProperty("environment", environment.EnvironmentName);

        var json = string.Equals(configuration[LogFormatKey], "Json", StringComparison.OrdinalIgnoreCase) ||
                   (!environment.IsDevelopment() && !string.Equals(configuration[LogFormatKey], "Text", StringComparison.OrdinalIgnoreCase));
        if (json)
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());   // @t, @l, @m, @tr, @sp, properties
        else
            logger.Enrich.With<TraceSuffixEnricher>().WriteTo.Console(outputTemplate: TextTemplate);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            logger.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlpEndpoint;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = serviceName,
                    ["deployment.environment.name"] = environment.EnvironmentName
                };
            });
        }
    }

    /// <summary>
    /// One log line per HTTP request (instead of ASP.NET Core's several): method, path
    /// without the query string, status, duration, and the caller - the subject ID of a
    /// person or the client ID of a machine, never a name or a token. 5xx are errors;
    /// health probes and metric scrapes are not logged.
    /// </summary>
    public static IApplicationBuilder UseEwpRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";
            options.GetLevel = (http, _, exception) =>
                IsProbe(http.Request.Path) ? LogEventLevel.Verbose
                : exception is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                var user = http.User;
                if (user.FindFirstValue("sub") is { } subject)
                    diagnostics.Set("subject", subject);
                if (user.FindFirstValue("client_id") is { } client)
                    diagnostics.Set("client", client);
            };
        });

    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments(MetricsEndpoints.Path);

    /// <summary>Microsoft.Extensions.Logging levels ("Logging:LogLevel") as Serilog minimum levels.</summary>
    private static void ApplyLevels(LoggerConfiguration logger, IConfigurationSection levels)
    {
        logger.MinimumLevel.Is(ToSerilog(levels["Default"]) ?? LogEventLevel.Information);
        // Every SQL statement at Information would bury the useful lines (the health checks
        // alone query every 30 s); a host can still lower it in Logging:LogLevel.
        logger.MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning);
        foreach (var category in levels.GetChildren())
        {
            if (category.Key != "Default" && ToSerilog(category.Value) is { } level)
                logger.MinimumLevel.Override(category.Key, level);
        }
    }

    private static LogEventLevel? ToSerilog(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" => LogEventLevel.Information,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "critical" or "none" => LogEventLevel.Fatal,
        _ => null
    };

    /// <summary>" trace=&lt;id&gt;" for the text format when the entry belongs to a trace; empty otherwise.</summary>
    private sealed class TraceSuffixEnricher : Serilog.Core.ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory factory) =>
            logEvent.AddPropertyIfAbsent(factory.CreateProperty(
                "TraceSuffix", logEvent.TraceId is { } trace ? $"  trace={trace}" : string.Empty));
    }
}