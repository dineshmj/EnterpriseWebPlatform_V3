using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseWebPlatform.Notifications.Api.Controllers;

/// <summary>
/// Unexpected exceptions become RFC 9457 problem details WITHOUT internals (OWASP A05 /
/// CWE-209): a generic message and a traceId; the full exception is logged under the
/// same traceId. Expected outcomes are mapped by the controllers.
/// </summary>
public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        var (status, title, detail) = exception switch
        {
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request", (string?)null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null)
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception. TraceId={TraceId}", traceId);

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = httpContext.Request.Path };
        problem.Extensions["traceId"] = traceId;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}