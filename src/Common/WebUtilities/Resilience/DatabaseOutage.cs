using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace EnterpriseWebPlatform.Common.WebUtilities.Resilience;

// ┌─ :: RESILIENCE + OBSERVABILITY :: A database outage answered quickly and plainly ──────────────
// │ Why : Sessions, key rings and every domain record live in PostgreSQL. While it is unreachable
// │       nothing signed-in can be served - but a person must not stare at a white frame for 15
// │       seconds and then get a raw error page, and a calling BFF must not wait for nothing.
// │ How : (1) Fail fast: every connection string gets a 5-second connect timeout (Npgsql's default
// │       is 15) unless it sets Timeout itself. (2) One middleware, early in the pipeline, catches
// │       ONLY "database unreachable" (an Npgsql connection failure, or the server shutting down /
// │       starting up / out of connections) and answers 503 with Retry-After: a page that tries
// │       again by itself for a page load (also inside the Shell's frame), Problem Details for an
// │       API call. Every other error keeps its own handling (APIs' handlers, the developer page).
// │       The answer carries the trace ID only - nothing about the database.
// │       Readiness (/health/ready) already reports the database, so the ingress stops routing
// │       to an instance; this covers the requests already on their way, and a single instance.
// │ Ref : doc/Containers.md | Features 1.7 (health) | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class DatabaseOutage
{
    /// <summary>Seconds a browser or caller is asked to wait before trying again.</summary>
    public const int RetryAfterSeconds = 15;

    private const int ConnectTimeoutSeconds = 5;

    /// <summary>
    /// The connection string with a 5-second connect timeout, unless it names one itself (an
    /// environment may need longer, e.g. across zones).
    /// </summary>
    public static string FailFast(string connectionString)
    {
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (given.ContainsKey("Timeout"))
            return connectionString;
        return new NpgsqlConnectionStringBuilder(connectionString) { Timeout = ConnectTimeoutSeconds }.ConnectionString;
    }

    /// <summary>True when the exception (or one it wraps) means the database cannot be reached at all.</summary>
    public static bool IsUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // ┌─ NOTE ─────────────────────────────────────────────────────────────────────
                // │ A PostgresException is an answer FROM the server: only "shutting down",
                // │ "starting up", "too many connections" and connection-class (08xxx) codes
                // │ mean an outage. A serialization failure (40001) is "transient" too, but the
                // │ server is up - it keeps its normal handling.
                // └────────────────────────────────────────────────────────────────────────────
                case PostgresException postgres:
                    return postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300"
                        || postgres.SqlState.StartsWith("08", StringComparison.Ordinal);
                // No answer at all: refused, timed out, connection broken.
                case NpgsqlException npgsql when npgsql.IsTransient:
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(IsUnavailable):
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Answers 503 + Retry-After when a request fails because the database is unreachable.
    /// Place it early - before authentication (which reads the session) - and, where the host
    /// has an exception handler, right AFTER UseExceptionHandler, so this one sees the error first.
    /// </summary>
    public static IApplicationBuilder UseEwpDatabaseOutageAnswer(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception exception) when (IsUnavailable(exception) && !context.Response.HasStarted)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(DatabaseOutage).FullName!);
                logger.LogWarning(
                    "Database unreachable: {Method} {Path} answered 503 ({ExceptionType}: {Message})",
                    context.Request.Method, context.Request.Path, exception.GetType().Name, exception.Message);
                await WriteAnswerAsync(context);
            }
        });

    private static async Task WriteAnswerAsync(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var response = context.Response;
        response.Clear();
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers.RetryAfter = RetryAfterSeconds.ToString();
        response.Headers.CacheControl = "no-store";

        if (IsPageLoad(context.Request))
        {
            response.ContentType = "text/html; charset=utf-8";
            await response.WriteAsync(Page(traceId));
            return;
        }

        response.ContentType = "application/problem+json";
        await response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
            title = "Temporarily unavailable",
            status = StatusCodes.Status503ServiceUnavailable,
            detail = $"A service this request depends on is not answering. Try again in {RetryAfterSeconds} seconds.",
            traceId
        }));
    }

    // A browser navigation (top-level or into a frame) gets a page; fetch / XHR calls get JSON.
    private static bool IsPageLoad(HttpRequest request)
    {
        var mode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (mode.Length > 0)
            return mode == "navigate";
        return request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────
    // │ No script and no inline style (it renders under every host's CSP); the reload is a meta
    // │ refresh. Only the trace ID is shown - the person can quote it, support can find the log.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    private static string Page(string traceId) => $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="refresh" content="{RetryAfterSeconds}">
        <title>Temporarily unavailable</title>
        </head>
        <body>
        <h1>Temporarily unavailable</h1>
        <p>A service this page depends on is not answering. This page tries again by itself in {RetryAfterSeconds} seconds.</p>
        <p>Reference: {WebUtility.HtmlEncode(traceId)}</p>
        </body>
        </html>
        """;
}