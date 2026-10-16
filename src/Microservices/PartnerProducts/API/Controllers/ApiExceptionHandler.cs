using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Controllers;

// ┌─ :: SECURITY :: Errors that leak nothing ──────────────────────────────────────────────────────
// │ Why : Stack traces, SQL or exception messages help an attacker map the system.
// │ How : Unexpected exceptions become RFC 9457 problem details with a traceId only; the domain's
// │       own refusals (400 rule, 409 conflict) carry their message - written for the counter.
// │ Ref : Features 2.4.1 | RFC 9457 | OWASP A05 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        var (status, title, detail) = exception switch
        {
            DigitalGoldRuleViolationException e => (StatusCodes.Status400BadRequest, "Domain rule violation", e.Message),
            DigitalGoldConflictException e => (StatusCodes.Status409Conflict, "Conflict", e.Message),
            PartnerBankUnavailableException e => (StatusCodes.Status503ServiceUnavailable, "Partner Bank unavailable", e.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request", null),
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