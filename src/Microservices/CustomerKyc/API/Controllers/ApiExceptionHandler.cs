using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

/// <summary>
/// Turns exceptions that escape the controllers into RFC 9457 problem details
/// WITHOUT leaking internals (OWASP A05 / CWE-209). Expected outcomes are already
/// mapped by the controllers; anything reaching here becomes a generic response
/// with a traceId, and the full exception is logged under the same traceId.
/// (Replaces the developer exception page, which would send stack traces in Development.)
/// </summary>
public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        var (status, title, detail) = exception switch
        {
            DomainRuleViolationException e => (StatusCodes.Status400BadRequest, "Domain rule violation", e.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request", null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null)
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception. TraceId={TraceId}", traceId);

        httpContext.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = traceId;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}
