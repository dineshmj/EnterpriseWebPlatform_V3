using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Authorization;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>A payments officer's decision: remarks are optional to approve, required to reject.</summary>
public sealed record PaymentDecisionRequest(string? Remarks);

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
    DecidePaymentCommandHandler decideHandler,
    RetryReleaseCommandHandler retryReleaseHandler,
    IPaymentsQueries queries,
    ApprovalTier approvalTier,
    ApprovalLimits approvalLimits) : ControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// The limits the screens explain up front (the API enforces them on every payment and
    /// decision): currency, approval threshold, maximum amount, reference length, and what
    /// the caller's clearance may approve (null = no limit).
    /// </summary>
    [HttpGet("policy")]
    [Authorize(Policy = "PaymentView")]
    public IActionResult GetPolicy() =>
        Ok(new
        {
            currency = PaymentRules.Aud,
            approvalThreshold = approvalTier.Threshold,
            maxAmount = PaymentRules.MaxAmount,
            referenceMaxLength = PaymentRules.ReferenceMaxLength,
            yourApprovalLimit = approvalLimits.MaxFor(ClearanceOf(User))
        });

    /// <summary>A payments officer approves a payment awaiting approval: the saga sends it.</summary>
    [HttpPost("{paymentId:long}/approve")]
    [Authorize(Policy = "PaymentApprove")]
    public Task<IActionResult> Approve(long paymentId, [FromBody] PaymentDecisionRequest? request, CancellationToken cancellationToken) =>
        DecideAsync(paymentId, approve: true, request?.Remarks, cancellationToken);

    /// <summary>A payments officer rejects a payment awaiting approval: the saga releases the funds.</summary>
    [HttpPost("{paymentId:long}/reject")]
    [Authorize(Policy = "PaymentReject")]
    public Task<IActionResult> Reject(long paymentId, [FromBody] PaymentDecisionRequest? request, CancellationToken cancellationToken) =>
        DecideAsync(paymentId, approve: false, request?.Remarks, cancellationToken);

    private async Task<IActionResult> DecideAsync(long paymentId, bool approve, string? remarks, CancellationToken cancellationToken)
    {
        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        var userId = User.FindFirst("sub")?.Value;
        if (branch is null || string.IsNullOrWhiteSpace(userId))
            return Forbid();

        try
        {
            var result = await decideHandler.HandleAsync(
                new DecidePaymentCommand(paymentId, approve, remarks, userId,
                    User.FindFirst("lan_id")?.Value ?? "a payments officer", ClearanceOf(User), branch),
                cancellationToken);

            return result is null ? NotFound() : Ok(new { status = result.Status, sagaStep = result.SagaStep });
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (Exception ex) when (ex is SeparationOfDutiesViolationException or ApprovalLimitExceededException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden, title: "Not permitted");
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Not awaiting approval");
        }
    }

    /// <summary>The highest clearance_level claim; 0 when absent (fail closed).</summary>
    private static int ClearanceOf(System.Security.Claims.ClaimsPrincipal user) =>
        user.Claims.Where(c => c.Type == "clearance_level")
            .Select(c => int.TryParse(c.Value, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

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

        var (allowed, branch) = PaymentsStaffAuthorizationHandler.ReadScopeOf(User);
        if (!allowed)
            return Forbid();

        return Ok(await queries.GetPaymentsAsync(branch, pageNumber, pageSize, statusFilter, cancellationToken));
    }

    /// <summary>
    /// The Payment Processing Monitor: every saga that is not finished, most urgent first
    /// (compensation failed, overdue, retrying, waiting for approval, running), with counts.
    /// </summary>
    [HttpGet("processing")]
    [Authorize(Policy = "PaymentView")]
    public async Task<IActionResult> GetProcessing(CancellationToken cancellationToken)
    {
        var (allowed, branch) = PaymentsStaffAuthorizationHandler.ReadScopeOf(User);
        if (!allowed)
            return Forbid();

        return Ok(await queries.GetProcessingAsync(branch, DateTimeOffset.UtcNow, cancellationToken));
    }

    /// <summary>
    /// Operations recovery: send the release of the reserved funds again for a payment whose
    /// compensation failed (COMPENSATION_FAILED). Any branch: the operations desk is central.
    /// </summary>
    [HttpPost("{paymentId:long}/retry-release")]
    [Authorize(Policy = "PaymentRetryRelease")]
    public async Task<IActionResult> RetryRelease(long paymentId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await retryReleaseHandler.HandleAsync(
                new RetryReleaseCommand(paymentId, User.FindFirst("lan_id")?.Value ?? "Operations"), cancellationToken);
            return result is null ? NotFound() : Ok(new { status = result.Status, sagaStep = result.SagaStep });
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to retry");
        }
    }

    /// <summary>The payment, its saga and the saga's timeline. A payment outside the caller's scope is reported as not found.</summary>
    [HttpGet("{paymentId:long}")]
    [Authorize(Policy = "PaymentView")]
    public async Task<IActionResult> GetPayment(long paymentId, CancellationToken cancellationToken)
    {
        var (allowed, branch) = PaymentsStaffAuthorizationHandler.ReadScopeOf(User);
        if (!allowed)
            return Forbid();

        var payment = await queries.GetPaymentAsync(paymentId, branch, cancellationToken);
        return payment is null ? NotFound() : Ok(payment);
    }
}