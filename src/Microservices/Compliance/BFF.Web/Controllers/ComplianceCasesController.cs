using System.Net.Http.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EnterpriseWebPlatform.Compliance.Bff.Web.Controllers;

public sealed record OfficerTextRequest(string? Text);

/// <summary>
/// The Compliance MFE's API. A thin, explicit facade: the BFF forwards the officer's
/// request with the officer's own access token and relays the Compliance API's answer.
/// It holds no business rules; branch scope, assignment (ReBAC), separation of duties
/// and the clearance a case's risk requires are all decided by the Compliance API.
/// </summary>
[Authorize]
[ApiController]
[Route("bff/api/compliance/cases")]
public sealed class ComplianceCasesController(
    IHttpClientFactory httpClientFactory,
    ILogger<ComplianceCasesController> logger) : ControllerBase
{
    /// <summary>Officer actions the API exposes; anything else is a 404 here, never forwarded.</summary>
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "claim", "release", "approve", "reject", "hold", "release-hold"
    };

    /// <summary>Actions whose body carries the officer's remarks or hold reason.</summary>
    private static readonly HashSet<string> ActionsWithText = new(StringComparer.Ordinal) { "approve", "reject", "hold" };

    [HttpGet]
    public Task<IActionResult> GetCases(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"/v1/compliance/cases?pageNumber={pageNumber}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status))
            query += $"&status={Uri.EscapeDataString(status)}";

        return SendAsync(new HttpRequestMessage(HttpMethod.Get, query), cancellationToken);
    }

    [HttpGet("{caseId:long}")]
    public Task<IActionResult> GetCase(long caseId, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/compliance/cases/{caseId}"), cancellationToken);

    // Not "{action}": "action" is a reserved MVC route value (it selects the controller method).
    [HttpPost("{caseId:long}/{officerAction}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Act(
        long caseId,
        string officerAction,
        [FromBody] OfficerTextRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!Actions.Contains(officerAction))
            return Task.FromResult<IActionResult>(NotFound());

        var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/compliance/cases/{caseId}/{officerAction}");
        if (ActionsWithText.Contains(officerAction))
            message.Content = JsonContent.Create(new OfficerTextRequest(request?.Text));

        return SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Calls the Compliance API and relays its status and JSON body (including
    /// its { error } explanations, e.g. a separation-of-duties refusal). When the
    /// API is unreachable or its circuit is open, answers 503 instead of hanging.
    /// </summary>
    private async Task<IActionResult> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using (message)
        {
            try
            {
                var client = httpClientFactory.CreateClient("ComplianceApi");
                using var response = await client.SendAsync(message, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                return new ContentResult
                {
                    StatusCode = (int)response.StatusCode,
                    Content = body,
                    ContentType = "application/json"
                };
            }
            catch (Exception ex) when (ex is BrokenCircuitException or TimeoutRejectedException or HttpRequestException)
            {
                logger.LogWarning(ex, "Compliance API unavailable for {Method} {Path}.", message.Method, message.RequestUri);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "The Compliance service is temporarily unavailable. Please try again shortly."
                });
            }
        }
    }
}