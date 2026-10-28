using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd;

using LaunchDarklyConfiguration = LaunchDarkly.Sdk.Server.Configuration;
using LaunchDarklyProvider = LaunchDarkly.OpenFeature.ServerProvider.Provider;

namespace EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;

// ┌─ :: ARCHITECTURE + SECURITY :: Feature flags through OpenFeature ──────────────────────────────
// │ Why : Some switches must change without a redeploy - a kill switch that pauses a product, a
// │       release toggle that hides a new API surface. The code asks a vendor-neutral API, so the
// │       flags can live in a file today and in LaunchDarkly (the customer's tool) tomorrow.
// │ How : EWP_LAUNCHDARKLY_SDK_KEY set -> LaunchDarkly; else FeatureFlags:File set -> that flag
// │       file, read in-process by flagd's file resolver (re-read every 5 s, no flagd server);
// │       else no source. Every check names its own default, and that default is TODAY's
// │       behaviour: a missing file, a broken file or LaunchDarkly being unreachable never
// │       changes the platform by itself - it logs a warning.
// │       Flags are switches, never permissions: who may do what stays with the authorization
// │       policies (Authorization-Model), a flag can only switch something OFF for everyone.
// │ Ref : B8 | OpenFeature specification | flagd flag definitions | OWASP A01 (no authz by flag)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class FeatureFlagKeys
{
    /// <summary>Release toggle: the BIAN /v2 endpoints answer; off = 404, as if they did not exist. Default on.</summary>
    public const string BianV2Endpoints = "bian-v2-endpoints";

    /// <summary>Kill switch: no new Digital Gold purchase is recorded or sent; purchases already sent carry on. Default off.</summary>
    public const string DigitalGoldSalesPaused = "digital-gold-sales-paused";
}

/// <summary>The one place a service asks for a flag: its value, or the caller's default when the flag cannot be read.</summary>
public sealed class EwpFeatureFlags(IFeatureClient client, EvaluationContext context, ILogger<EwpFeatureFlags> logger)
{
    public async Task<bool> IsOnAsync(string flag, bool defaultValue, CancellationToken cancellationToken = default)
    {
        var details = await client.GetBooleanDetailsAsync(flag, defaultValue, context, cancellationToken: cancellationToken);
        if (details.ErrorType != ErrorType.None)
        {
            // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
            // │ A flag that cannot be read is visible in the log (and so in the log search), with
            // │ the default that was used instead - never an error for the caller.
            // └──────────────────────────────────────────────────────────────────────────────────────
            logger.LogWarning("Feature flag {Flag} could not be read ({Error}: {Message}); its default {Default} was used.",
                flag, details.ErrorType, details.ErrorMessage, defaultValue);
        }

        return details.Value;
    }
}

/// <summary>
/// An endpoint (controller or action) that answers only while a flag is on. When it is off the
/// endpoint answers 404 with an empty body - exactly like a route that does not exist.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class FeatureGateAttribute(string flag, bool defaultValue) : Attribute
{
    public string Flag { get; } = flag;

    public bool DefaultValue { get; } = defaultValue;
}

public sealed class FeatureGateMiddleware(RequestDelegate next, EwpFeatureFlags flags)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var gate = context.GetEndpoint()?.Metadata.GetMetadata<FeatureGateAttribute>();

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A switched-off endpoint answers like no endpoint at all (404, no body), BEFORE
        // │ authentication: nothing about it, not even "you may not", is given away. A switched-
        // │ on endpoint goes on to authentication and its own authorization policy unchanged.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (gate is not null && !await flags.IsOnAsync(gate.Flag, gate.DefaultValue, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class FeatureFlagRegistration
{
    public const string LaunchDarklySdkKeyKey = "EWP_LAUNCHDARKLY_SDK_KEY";
    public const string FileKey = "FeatureFlags:File";

    /// <summary>
    /// Feature flags for this service (<paramref name="serviceName"/> is how it shows in LaunchDarkly):
    /// <see cref="EwpFeatureFlags"/> to ask, and the flag source connected at start-up without delaying it.
    /// </summary>
    public static IHostApplicationBuilder AddEwpFeatureFlags(this IHostApplicationBuilder builder, string serviceName)
    {
        var context = EvaluationContext.Builder()
            .SetTargetingKey(serviceName)
            .Set("kind", "service")
            .Build();

        builder.Services.AddSingleton(services => new EwpFeatureFlags(
            Api.Instance.GetClient(serviceName), context, services.GetRequiredService<ILogger<EwpFeatureFlags>>()));
        builder.Services.AddHostedService(services => new FeatureFlagSourceConnection(
            builder.Configuration, builder.Environment.ContentRootPath, services.GetRequiredService<ILoggerFactory>()));
        return builder;
    }

    /// <summary>Endpoints marked <see cref="FeatureGateAttribute"/> answer 404 while their flag is off. Goes before UseAuthentication.</summary>
    public static IApplicationBuilder UseEwpFeatureGates(this IApplicationBuilder app) => app.UseMiddleware<FeatureGateMiddleware>();
}

/// <summary>Connects the flag source when the service starts and disconnects it when it stops.</summary>
internal sealed class FeatureFlagSourceConnection(IConfiguration configuration, string contentRoot, ILoggerFactory loggers) : IHostedService
{
    private readonly ILogger _logger = loggers.CreateLogger<FeatureFlagSourceConnection>();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var sdkKey = configuration[FeatureFlagRegistration.LaunchDarklySdkKeyKey];
        var file = configuration[FeatureFlagRegistration.FileKey];

        FeatureProvider provider;
        string source;
        if (!string.IsNullOrWhiteSpace(sdkKey))
        {
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The LaunchDarkly SDK key is a SECRET (it reads every flag of its environment):
            // │ from the environment or the secret store only, never a settings file, never logged.
            // │ The SDK keeps one outbound stream to LaunchDarkly (443) - see Kubernetes.md egress.
            // └──────────────────────────────────────────────────────────────────────────────────────
            provider = new LaunchDarklyProvider(LaunchDarklyConfiguration.Builder(sdkKey.Trim())
                .Logging(LaunchDarkly.Logging.Logs.CoreLogging(loggers))
                .Build());
            source = "LaunchDarkly";
        }
        else if (!string.IsNullOrWhiteSpace(file))
        {
            var path = Path.GetFullPath(file.Trim(), contentRoot);
            provider = new FlagdProvider(new FlagdConfigBuilder()
                .WithResolverType(ResolverType.FILE)
                .WithOfflineFlagSourcePath(path)
                .WithDeadlineMs(10_000)
                .Build());
            source = $"the flag file {path}";
        }
        else
        {
            _logger.LogWarning("No feature flag source ({SdkKey} or {File}): every flag keeps its default.",
                FeatureFlagRegistration.LaunchDarklySdkKeyKey, FeatureFlagRegistration.FileKey);
            return Task.CompletedTask;
        }

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ Connected in the background: the service starts and answers at once, with every
        // │ flag at its default until the source is ready - a flag source that is slow or down
        // │ never stops the service from starting.
        // └──────────────────────────────────────────────────────────────────────────────────────
        _ = ConnectAsync(provider, source);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Api.Instance.ShutdownAsync();

    private async Task ConnectAsync(FeatureProvider provider, string source)
    {
        try
        {
            await Api.Instance.SetProviderAsync(provider);
            var status = Api.Instance.GetClient().ProviderStatus;
            if (status == ProviderStatus.Ready)
                _logger.LogInformation("Feature flags come from {Source}.", source);
            else
                _logger.LogWarning("Feature flags from {Source} are not ready ({Status}): every flag keeps its default until they are.", source, status);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Feature flags could not be read from {Source}: every flag keeps its default until it can.", source);
        }
    }
}