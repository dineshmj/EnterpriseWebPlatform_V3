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

public sealed record ResendParkedMessageBody(string? Reason);

public sealed record NetworkResolutionProposalBody(string? Outcome, string? NetworkReference, string? Evidence);

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
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The path segment is one of these constants - never the caller's text - so nothing the
        // │ browser sends can steer the call to another Payments API endpoint.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var action = decision switch
        {
            "approve" => "approve",
            "reject" => "reject",
            "retry-release" => "retry-release",
            _ => null
        };
        if (action is null)
            return Task.FromResult<IActionResult>(NotFound());

        return SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/{action}") { Content = JsonContent.Create(new PaymentDecisionBody(body?.Remarks)) },
            cancellationToken);
    }

    [HttpGet("payments/processing")]
    public Task<IActionResult> GetProcessing(CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, "/v1/payments/processing"), cancellationToken);

    /// <summary>
    /// Manual reconciliation of a payment the network never confirmed: one operator proposes SENT /
    /// NOT_SENT with evidence; another approves or rejects (payment.reconcile, four eyes, recent MFA -
    /// all checked by the Payments API).
    /// </summary>
    [HttpPost("payments/{paymentId:long}/network-resolution")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ProposeNetworkResolution(long paymentId, [FromBody] NetworkResolutionProposalBody? body, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/network-resolution")
            {
                Content = JsonContent.Create(new NetworkResolutionProposalBody(body?.Outcome, body?.NetworkReference, body?.Evidence))
            },
            cancellationToken);

    [HttpPost("payments/{paymentId:long}/network-resolution/{decision}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DecideNetworkResolution(long paymentId, string decision, [FromBody] PaymentDecisionBody? body, CancellationToken cancellationToken = default)
    {
        // SEC: as above - a constant path segment, never the caller's text.
        var action = decision switch
        {
            "approve" => "approve",
            "reject" => "reject",
            _ => null
        };
        if (action is null)
            return Task.FromResult<IActionResult>(NotFound());

        return SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/network-resolution/{action}") { Content = JsonContent.Create(new PaymentDecisionBody(body?.Remarks)) },
            cancellationToken);
    }

    /// <summary>Messages the Payments Outbox relay parked (operations, outbox.view).</summary>
    [HttpGet("payments/outbox/parked")]
    public Task<IActionResult> GetParkedMessages(CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient, new HttpRequestMessage(HttpMethod.Get, "/v1/payments/outbox/parked"), cancellationToken);

    /// <summary>Operations resend a parked message (workflow.reprocess, a reason, recent MFA - checked by the Payments API).</summary>
    [HttpPost("payments/outbox/{messageId:guid}/resend")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ResendParkedMessage(Guid messageId, [FromBody] ResendParkedMessageBody? body, CancellationToken cancellationToken = default) =>
        SendAsync(PaymentsBffOptions.PaymentsApiClient,
            new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/outbox/{messageId}/resend") { Content = JsonContent.Create(new ResendParkedMessageBody(body?.Reason)) },
            cancellationToken);

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
    /// Records cash paid in at the counter (Accounts API). Like a payment, the screen creates
    /// one Idempotency-Key per deposit form and it is passed through unchanged, so pressing
    /// Deposit twice (or a resend after a lost answer) never credits the account twice.
    /// </summary>
    [HttpPost("cash-deposits")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RecordCashDeposit(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] JsonElement deposit,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "An Idempotency-Key header (a GUID) is required." }));
        if (deposit.ValueKind != JsonValueKind.Object)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "A deposit is required." }));

        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/accounts/cash-deposits") { Content = JsonContent.Create(deposit) };
        message.Headers.Add("Idempotency-Key", key.ToString());
        return SendAsync(PaymentsBffOptions.AccountsApiClient, message, cancellationToken);
    }

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
            // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
            // │ If the API is down or its circuit is open, answer 503 at once ("temporarily
            // │ unavailable") instead of letting the screen hang; the API's own answer (a 403, a
            // │ validation problem) is relayed unchanged.
            // └──────────────────────────────────────────────────────────────────────────────────────
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