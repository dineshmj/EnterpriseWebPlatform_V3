using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Authorization;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>A payment as the assisted-channel screen captures it.</summary>
public sealed record InitiatePaymentRequest(
    string? CustomerNumber,
    string? FromBsb,
    string? FromAccountNumber,
    string? PayeeName,
    string? ToBsb,
    string? ToAccountNumber,
    decimal Amount,
    string? Reference);

/// <summary>
/// Payments for staff. Starting a payment returns 202 Accepted at once: the payment and its
/// saga are recorded, and the saga carries it out in the background. The caller follows
/// it with GET (the status and the saga's timeline).
/// </summary>
[ApiController]
[Route("v1/payments")]
public sealed class PaymentsController(
    InitiatePaymentCommandHandler initiateHandler,
    IPaymentsQueries queries) : ControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// Starts a payment. The Idempotency-Key header (a GUID the screen creates once per
    /// payment form) makes repeating the request safe: the same key returns the same payment.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "PaymentInitiate")]
    public async Task<IActionResult> Initiate(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] InitiatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var paymentRef) || paymentRef == Guid.Empty)
            return ValidationProblem("An Idempotency-Key header (a GUID, one per payment) is required.");

        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        var userId = User.FindFirst("sub")?.Value;
        if (branch is null || string.IsNullOrWhiteSpace(userId))
            return Forbid();

        InitiatePaymentResult result;
        try
        {
            result = await initiateHandler.HandleAsync(
                new InitiatePaymentCommand(
                    paymentRef, request.CustomerNumber ?? string.Empty, request.FromBsb ?? string.Empty,
                    request.FromAccountNumber ?? string.Empty, request.PayeeName ?? string.Empty,
                    request.ToBsb ?? string.Empty, request.ToAccountNumber ?? string.Empty,
                    request.Amount, request.Reference, branch, userId),
                cancellationToken);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Idempotency-Key conflict");
        }

        var location = $"/v1/payments/{result.PaymentId}";
        var body = new { paymentId = result.PaymentId, paymentNumber = result.PaymentNumber, status = result.Status, created = result.Created };
        Response.Headers.Location = location;
        return result.Created ? Accepted(location, body) : Ok(body);
    }

    [HttpGet]
    [Authorize(Policy = "PaymentView")]
    public async Task<IActionResult> GetPayments(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize < 1 || pageSize > MaxPageSize)
            return ValidationProblem($"pageNumber must be at least 1 and pageSize between 1 and {MaxPageSize}.");

        PaymentStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!PaymentsCodes.TryParsePaymentStatus(status, out var parsed))
                return ValidationProblem($"Unknown status '{status}'.");
            statusFilter = parsed;
        }

        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        if (branch is null)
            return Forbid();

        return Ok(await queries.GetPaymentsAsync(branch, pageNumber, pageSize, statusFilter, cancellationToken));
    }

    /// <summary>The payment, its saga and the saga's timeline. Another branch's payment is reported as not found.</summary>
    [HttpGet("{paymentId:long}")]
    [Authorize(Policy = "PaymentView")]
    public async Task<IActionResult> GetPayment(long paymentId, CancellationToken cancellationToken)
    {
        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        if (branch is null)
            return Forbid();

        var payment = await queries.GetPaymentAsync(paymentId, branch, cancellationToken);
        return payment is null ? NotFound() : Ok(payment);
    }
}