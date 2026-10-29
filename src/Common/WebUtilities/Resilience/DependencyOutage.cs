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

// ┌─ :: RESILIENCE + OBSERVABILITY :: An outage of a dependency answered quickly and plainly ───────
// │ Why : Sessions, key rings and every domain record live in PostgreSQL, and every sign-in goes
// │       through the IDP. While one of them is unreachable nothing signed-in can be served - but a
// │       person must not stare at a white frame and then get a raw error page, and a calling BFF
// │       must not wait for nothing.
// │ How : (1) Fail fast: every connection string gets a 5-second connect timeout (Npgsql's default
// │       is 15) unless it sets Timeout itself. (2) One middleware, early in the pipeline, catches
// │       ONLY "a dependency is unavailable" and answers 503 with Retry-After: a page that tries
// │       again by itself for a page load (also inside the Shell's frame), Problem Details for an
// │       API call. Unavailable = the database cannot be reached (connection failure; server
// │       shutting down / starting up / out of connections); the sign-in against the IDP fails
// │       (PAR, discovery - e.g. the IDP's own database is down and it answers 503); or an HTTP
// │       dependency cannot be reached, times out or answers 502 / 503 / 504. Every other error
// │       keeps its own handling (APIs' handlers, the developer page). The answer carries the
// │       trace ID only - nothing about the dependency.
// │       Readiness (/health/ready) already reports the database, so the ingress stops routing
// │       to an instance; this covers the requests already on their way, and a single instance.
// │ Ref : doc/Containers.md | Features 1.7 (health) | RFC 9110 §15.6.4 (503), §10.2.3 (Retry-After)
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class DependencyOutage
{
    /// <summary>Seconds a browser or caller is asked to wait before trying again.</summary>
    public const int RetryAfterSeconds = 15;

    private const int ConnectTimeoutSeconds = 5;

    private const string OpenIdConnectHandler = "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler";

    /// <summary>The outage page's stylesheet: served by every host itself, so it needs no dependency.</summary>
    public const string StylesheetPath = "/ewp-outage.css";

    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────────
    // │ Every host's CSP allows stylesheets from 'self' only (no inline style), so the page links
    // │ this small stylesheet, answered by the middleware before anything that needs a dependency.
    // │ The same rules are in the Next.js BFFs' server.mjs and the KYC BFF's outage.ts.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public const string Stylesheet = """
        :root { color-scheme: light dark; }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f4f6f8; color: #1f2933;
               font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        main { max-width: 32rem; margin: 1.5rem; padding: 2rem 2.25rem; background: #fff; border-radius: 12px;
               border-top: 4px solid #d97706; box-shadow: 0 1px 3px rgba(0, 0, 0, .08), 0 8px 24px rgba(0, 0, 0, .06); }
        h1 { margin: 0 0 .75rem; font-size: 1.35rem; font-weight: 600; }
        p { margin: .5rem 0; line-height: 1.5; }
        .ref { font-size: .8rem; color: #6b7280; }
        @media (prefers-color-scheme: dark) {
          body { background: #111827; color: #e5e7eb; }
          main { background: #1f2937; box-shadow: none; }
          .ref { color: #9ca3af; }
        }
        """;

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

    /// <summary>True when the exception (or one it wraps) means a dependency cannot serve the request at all.</summary>
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
                // The database gave no answer at all: refused, timed out, connection broken.
                case NpgsqlException npgsql when npgsql.IsTransient:
                    return true;
                // An HTTP dependency: no connection, or it says it cannot serve (502 / 503 / 504).
                // Other statuses (400, 404, 500 ...) are that dependency's answer, not an outage.
                case HttpRequestException http
                    when http.StatusCode is null or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                    return true;
                // HttpClient's own timeout.
                case TaskCanceledException { InnerException: TimeoutException }:
                    return true;
                // ┌─ NOTE ─────────────────────────────────────────────────────────────────────
                // │ The sign-in handler failing while it talks to the IDP (pushed authorization
                // │ request, discovery): the IDP is unavailable - e.g. its database is down and
                // │ it answered 503, which the handler reports as "invalid response". Its
                // │ configuration errors stop the start-up, not a request.
                // └────────────────────────────────────────────────────────────────────────────
                case InvalidOperationException when IsFrom(current, OpenIdConnectHandler):
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(IsUnavailable):
                    return true;
            }
        }
        return false;
    }

    // The type that threw - an async method's state machine is nested in it.
    private static bool IsFrom(Exception exception, string typeName) =>
        exception.TargetSite?.DeclaringType?.FullName?.StartsWith(typeName, StringComparison.Ordinal) == true;

    /// <summary>
    /// Answers 503 + Retry-After when a request fails because a dependency is unavailable.
    /// Place it early - before authentication (which reads the session) - and AFTER any
    /// UseExceptionHandler or UseDeveloperExceptionPage: the inner middleware sees an error first,
    /// so placed before them, the outage would become their 500 page instead.
    /// </summary>
    public static IApplicationBuilder UseEwpOutageAnswer(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == StylesheetPath)
            {
                context.Response.ContentType = "text/css; charset=utf-8";
                context.Response.Headers.CacheControl = "public, max-age=86400";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                await context.Response.WriteAsync(Stylesheet);
                return;
            }

            try
            {
                await next(context);
            }
            catch (Exception exception) when (IsUnavailable(exception) && !context.Response.HasStarted)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(DependencyOutage).FullName!);
                logger.LogWarning(
                    "Dependency unavailable: {Method} {Path} answered 503 ({ExceptionType}: {Message})",
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
            await response.WriteAsync(Page(traceId), context.RequestAborted);
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
        }), context.RequestAborted);
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
    // │ No script and no inline style (it renders under every host's CSP: the look comes from
    // │ /ewp-outage.css, served above); the reload is a meta refresh. Only the trace ID is shown -
    // │ the person can quote it, support can find the log.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    private static string Page(string traceId) => $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="refresh" content="{RetryAfterSeconds}">
        <title>Temporarily unavailable</title>
        <link rel="stylesheet" href="{StylesheetPath}">
        </head>
        <body>
        <main>
        <h1>Temporarily unavailable</h1>
        <p>A service this page depends on is not answering. This page tries again by itself in {RetryAfterSeconds} seconds.</p>
        <p class="ref">Reference: {WebUtility.HtmlEncode(traceId)}</p>
        </main>
        </body>
        </html>
        """;
}