using System.Net.Http.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EnterpriseWebPlatform.Accounts.Bff.Web.Controllers;

public sealed record OfficerDecisionRequest(string? Text, string? Product);

/// <summary>
/// The Accounts MFE's API. A thin, explicit facade: the BFF forwards the officer's
/// request with the officer's own access token and relays the Accounts API's answer.
/// It holds no business rules; branch scope, assignment (ReBAC) and separation of
/// duties are all decided by the Accounts API.
/// </summary>
[Authorize]
[ApiController]
[Route("bff/api/accounts")]
public sealed class AccountsApiController(
    IHttpClientFactory httpClientFactory,
    ILogger<AccountsApiController> logger) : ControllerBase
{
    /// <summary>Officer actions the API exposes; anything else is a 404 here, never forwarded.</summary>
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "claim", "release", "approve", "reject", "hold", "release-hold"
    };

    /// <summary>Actions whose body carries remarks / hold reason (and, for approve, the product).</summary>
    private static readonly HashSet<string> ActionsWithBody = new(StringComparer.Ordinal) { "approve", "reject", "hold" };

    [HttpGet("applications")]
    public Task<IActionResult> GetApplications(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"/v1/accounts/applications?pageNumber={pageNumber}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status))
            query += $"&status={Uri.EscapeDataString(status)}";

        return SendAsync(new HttpRequestMessage(HttpMethod.Get, query), cancellationToken);
    }

    [HttpGet("applications/{applicationId:long}")]
    public Task<IActionResult> GetApplication(long applicationId, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts/applications/{applicationId}"), cancellationToken);

    // Not "{action}": "action" is a reserved MVC route value (it selects the controller method).
    [HttpPost("applications/{applicationId:long}/{officerAction}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Act(
        long applicationId,
        string officerAction,
        [FromBody] OfficerDecisionRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!Actions.Contains(officerAction))
            return Task.FromResult<IActionResult>(NotFound());

        var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/applications/{applicationId}/{officerAction}");
        if (ActionsWithBody.Contains(officerAction))
            message.Content = JsonContent.Create(new OfficerDecisionRequest(request?.Text, request?.Product));

        return SendAsync(message, cancellationToken);
    }

    [HttpGet("accounts")]
    public Task<IActionResult> GetAccounts(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts?pageNumber={pageNumber}&pageSize={pageSize}"), cancellationToken);

    /// <summary>
    /// Calls the Accounts API and relays its status and JSON body (including its
    /// { error } explanations, e.g. a separation-of-duties refusal). When the API is
    /// unreachable or its circuit is open, answers 503 instead of hanging.
    /// </summary>
    private async Task<IActionResult> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using (message)
        {
            try
            {
                var client = httpClientFactory.CreateClient("AccountsApi");
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
                logger.LogWarning(ex, "Accounts API unavailable for {Method} {Path}.", message.Method, message.RequestUri);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "The Accounts service is temporarily unavailable. Please try again shortly."
                });
            }
        }
    }
}