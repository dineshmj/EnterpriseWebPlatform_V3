using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Polly.CircuitBreaker;
using Polly.Timeout;

using EnterpriseWebPlatform.Payments.Bff.Web.Configuration;

namespace EnterpriseWebPlatform.Payments.Bff.Web.Controllers;

public sealed record PayeeConfirmationBody(string? Bsb, string? AccountNumber, string? AccountName);

public sealed record PaymentDecisionBody(string? Remarks);

/// <summary>
/// The Payments MFE's API. A thin, explicit facade: the BFF forwards each request with the
/// staff member's own access token and relays the API's answer. It holds no business
/// rules; branch scope, permissions and every payment rule are decided by the Payments API
/// (and, for the paying accounts, by the Accounts API).
/// </summary>
[Authorize]
[ApiController]
[Route("bff/api")]
public sealed class PaymentsApiController(
    IHttpClientFactory httpClientFactory,
    ILogger<PaymentsApiController> logger) : ControllerBase
{
    [HttpGet("payments")]
    public Task<IActionResult> GetPayments(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"/v1/payments?pageNumber={pageNumber}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status))
            query += $"&status={Uri.EscapeDataString(status)}";

        return SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, query), cancellationToken);
    }

    [HttpGet("payments/{paymentId:long}")]
    public Task<IActionResult> GetPayment(long paymentId, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}"), cancellationToken);

    /// <summary>
    /// Starts a payment. The screen creates one Idempotency-Key per payment form; it is
    /// passed through unchanged, so pressing Transfer twice (or a resend after a lost
    /// answer) finds the same payment instead of creating a second one.
    /// </summary>
    [HttpPost("payments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Initiate(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] JsonElement payment,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "An Idempotency-Key header (a GUID) is required." }));
        if (payment.ValueKind != JsonValueKind.Object)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "A payment is required." }));

        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/payments") { Content = JsonContent.Create(payment) };
        message.Headers.Add("Idempotency-Key", key.ToString());
        return SendAsync(PaymentsBffOptions.PaymentsApiClient, message, cancellationToken);
    }

    /// <summary>
    /// A payments officer's decision (approve / reject: role, branch, clearance limit and SoD are
    /// checked by the Payments API), or operations retrying a failed release (retry-release).
    /// </summary>
    [HttpPost("payments/{paymentId:long}/{decision}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Decide(long paymentId, string decision, [FromBody] PaymentDecisionBody? body, CancellationToken cancellationToken = default)
    {
        if (decision is not ("approve" or "reject" or "retry-release"))
            return Task.FromResult<IActionResult>(NotFound());

        return SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/{decision}") { Content = JsonContent.Create(new PaymentDecisionBody(body?.Remarks)) },
            cancellationToken);
    }

    [HttpGet("payments/processing")]
    public Task<IActionResult> GetProcessing(CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, "/v1/payments/processing"), cancellationToken);

    [HttpGet("payments/policy")]
    public Task<IActionResult> GetPolicy(CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, "/v1/payments/policy"), cancellationToken);

    [HttpGet("payments/bsb/{bsb}")]
    public Task<IActionResult> LookupBsb(string bsb, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/bsb/{Uri.EscapeDataString(bsb)}"), cancellationToken);

    [HttpPost("payments/payee-confirmations")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ConfirmPayee([FromBody] PayeeConfirmationBody body, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, "/v1/payments/payee-confirmations") { Content = JsonContent.Create(body) },
            cancellationToken);

    /// <summary>The customer's paying accounts (Accounts API, read-only): holder, account, available amount.</summary>
    [HttpGet("payer-accounts")]
    public Task<IActionResult> FindPayerAccounts([FromQuery] string? search, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.AccountsApiClient,
            new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts/for-payment?search={Uri.EscapeDataString(search ?? string.Empty)}"),
            cancellationToken);

    /// <summary>
    /// Calls the API and relays its status and JSON body (including its explanations, e.g.
    /// a validation problem). When the API is unreachable or its circuit is open, answers
    /// 503 instead of hanging.
    /// </summary>
    private async Task<IActionResult> SendAsync(string clientName, HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using (message)
        {
            try
            {
                var client = httpClientFactory.CreateClient(clientName);
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
                logger.LogWarning(ex, "{Api} unavailable for {Method} {Path}.", clientName, message.Method, message.RequestUri);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "The service is temporarily unavailable. Please try again shortly."
                });
            }
        }
    }
}