using System.Security.Claims;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>Limits per minute (configuration section "RateLimiting"; the defaults suit normal use comfortably).</summary>
public sealed class EwpRateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    /// <summary>Requests per minute by one signed-in person (reads and changes together).</summary>
    public int PerPersonPerMinute { get; init; } = 600;

    /// <summary>Changes per minute by one signed-in person (POST, PUT, PATCH, DELETE): payments, decisions, uploads.</summary>
    public int ChangesPerPersonPerMinute { get; init; } = 60;

    /// <summary>Requests per minute from one IP address before sign-in.</summary>
    public int AnonymousPerIpPerMinute { get; init; } = 120;

    /// <summary>Requests per minute by one machine client (an M2M token with no person behind it).</summary>
    public int PerMachineClientPerMinute { get; init; } = 3000;
}

/// <summary>
/// Rate limiting for every .NET BFF and API (OWASP API4: unrestricted resource consumption).
/// Partitioned by who is calling - never one shared bucket that one user could exhaust for all:
///   - a signed-in person: by subject ID, with a tighter limit for changes than for reads;
///   - a machine client: by client ID (subscribers, BFF-to-API M2M calls);
///   - nobody signed in yet: by IP address.
/// Only the given path prefixes are limited (the API surface); static files, health probes,
/// SignalR hubs and the subscribers' internal endpoints are not. Over the limit the answer is
/// 429 with Retry-After and a problem-details body the screens show as-is.
/// </summary>
public static class RateLimitingExtensions
{
    private static readonly string[] AlwaysExempt = ["/health", "/hubs", "/internal"];

    public static IServiceCollection AddEwpRateLimiting(this IServiceCollection services, IConfiguration configuration, params string[] limitedPathPrefixes)
    {
        var options = configuration.GetSection(EwpRateLimitingOptions.SectionName).Get<EwpRateLimitingOptions>() ?? new EwpRateLimitingOptions();
        if (!options.Enabled)
            return services;

        bool IsLimited(HttpContext context)
        {
            var path = context.Request.Path;
            return !AlwaysExempt.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) &&
                   limitedPathPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Every request on the limited surface counts against its caller's partition ...
            var perCaller = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IsLimited(context))
                    return RateLimitPartition.GetNoLimiter("exempt");

                var (key, limit) = CallerOf(context.User, context, options);
                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => Window(limit));
            });

            // ... and a change additionally counts against the person's tighter "changes" budget.
            var perPersonChanges = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var person = context.User.FindFirst("sub")?.Value;
                if (!IsLimited(context) || person is null || HttpMethods.IsGet(context.Request.Method) ||
                    HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method))
                {
                    return RateLimitPartition.GetNoLimiter("exempt");
                }

                return RateLimitPartition.GetSlidingWindowLimiter($"changes:{person}", _ => Window(options.ChangesPerPersonPerMinute));
            });

            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(perCaller, perPersonChanges);

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? (int)Math.Ceiling(wait.TotalSeconds)
                    : 10;

                http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("EnterpriseWebPlatform.RateLimiting").LogWarning(
                    "Rate limit reached for {Caller} on {Method} {Path}; retry after {RetryAfter} s.",
                    CallerOf(http.User, http, options).Key, http.Request.Method, http.Request.Path, retryAfter);

                http.Response.ContentType = "application/problem+json";
                await http.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc6585#section-4",
                    title = "Too many requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = $"Too many requests in a short time. Please wait {retryAfter} seconds and try again."
                }, cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Adds the limiter to the pipeline - after authentication (it partitions by caller), before
    /// the endpoints. Does nothing when rate limiting is disabled in configuration.
    /// </summary>
    public static IApplicationBuilder UseEwpRateLimiting(this IApplicationBuilder app) =>
        app.ApplicationServices.GetService<IOptions<RateLimiterOptions>>()?.Value.GlobalLimiter is null
            ? app
            : app.UseRateLimiter();

    private static (string Key, int Limit) CallerOf(ClaimsPrincipal user, HttpContext context, EwpRateLimitingOptions options)
    {
        if (user.FindFirst("sub")?.Value is { Length: > 0 } person)
            return ($"person:{person}", options.PerPersonPerMinute);
        if (user.Identity?.IsAuthenticated == true && user.FindFirst("client_id")?.Value is { Length: > 0 } client)
            return ($"client:{client}", options.PerMachineClientPerMinute);
        return ($"ip:{context.Connection.RemoteIpAddress}", options.AnonymousPerIpPerMinute);
    }

    private static SlidingWindowRateLimiterOptions Window(int permitsPerMinute) => new()
    {
        PermitLimit = Math.Max(1, permitsPerMinute),
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 0,
        AutoReplenishment = true
    };
}