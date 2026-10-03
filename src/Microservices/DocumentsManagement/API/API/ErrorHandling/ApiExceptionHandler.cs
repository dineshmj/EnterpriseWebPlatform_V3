using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

namespace EnterpriseWebPlatform.DocumentsManagement.API.ErrorHandling;

/// <summary>
/// Turns exceptions into RFC 9457 problem details WITHOUT leaking internals
/// (OWASP A05 / CWE-209). Only this service's domain errors carry their message;
/// everything else becomes a generic response with a traceId, and the full
/// exception is logged under the same traceId.
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
            DomainRuleViolationException e => (StatusCodes.Status422UnprocessableEntity, "Domain rule violation", e.Message),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found", null),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request", null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null)
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception. TraceId={TraceId}", traceId);
        else
            logger.LogInformation("Request rejected with {Status} ({ExceptionType}). TraceId={TraceId}", status, exception.GetType().Name, traceId);

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
