using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.ErrorHandling;

// ┌─ :: SECURITY :: Errors that leak nothing ──────────────────────────────────────────────────────
// │ Why : Stack traces, SQL or exception messages help an attacker map the system.
// │ How : Unexpected exceptions become problem details with a traceId only; domain rule violations
// │       carry their own message; everything is logged under the traceId.
// │ Ref : Features 2.4.1 | RFC 9457 | OWASP A05 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Turns exceptions into RFC 9457 problem details WITHOUT leaking internals
/// (OWASP A05 / CWE-209). Only exception types this service throws on purpose,
/// with messages written for callers, are passed through. Everything else -
/// framework, EF Core, Npgsql, null references - becomes a generic 500 carrying
/// only a traceId; the full exception is logged under the same traceId.
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
            ConcurrencyConflictException e => (StatusCodes.Status409Conflict, "Concurrency conflict", e.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency conflict", "The resource was modified by another request. Reload and try again."),
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