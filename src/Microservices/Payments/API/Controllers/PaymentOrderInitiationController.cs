using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Authorization;
using EnterpriseWebPlatform.Payments.Api.Bian;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: /v2 = BIAN PaymentOrderInitiation, next to /v1 ─────────────────
// │ Why : Shows the Payments API speaking BIAN's standard contract (release 14.0.0) to anyone who
// │       expects it - a vendor, a partner bank's tooling - while EWP's own callers keep /v1.
// │ How : BIAN's three operations for a domestic transfer, each on the SAME command handlers,
// │       queries and policies as /v1 - one business path, two contracts:
// │         POST v2/PaymentOrderInitiation/Initiate                       (PaymentInitiate)
// │         GET  v2/PaymentOrderInitiation/{id}/Retrieve                  (PaymentView, branch scope)
// │         PUT  v2/PaymentOrderInitiation/{id}/OrderInitiation/approval/Exchange
// │              Task "Approve" -> PaymentApprove, "Reject" -> PaymentReject (both MFA step-up,
// │              separation of duties and approval limits as in /v1)
// │       The Exchange checks its policy per decision (IAuthorizationService): the body, not the
// │       route, says which it is. JSON keeps BIAN's PascalCase names (BianJson).
// │ Ref : doc/bian/README.md | BIAN PaymentOrderInitiation.yaml (release 14.0.0) | OWASP A01
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("v2/PaymentOrderInitiation")]
public sealed class PaymentOrderInitiationController(
    InitiatePaymentCommandHandler initiateHandler,
    DecidePaymentCommandHandler decideHandler,
    IPaymentsQueries queries,
    IAuthorizationService authorization,
    ClientIds clientIds) : ControllerBase
{
    /// <summary>BIAN Initiate: starts a payment. The Idempotency-Key header works exactly as in /v1.</summary>
    [HttpPost("Initiate")]
    [Authorize(Policy = "PaymentInitiate")]
    public async Task<IActionResult> Initiate(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PaymentOrderInitiationTransaction? request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var paymentRef) || paymentRef == Guid.Empty)
            return ValidationProblem("An Idempotency-Key header (a GUID, one per payment) is required.");

        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);
        if (order is null)
            return ValidationProblem(string.Join(" ", errors));

        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        var userId = User.FindFirst("sub")?.Value;
        if (branch is null || string.IsNullOrWhiteSpace(userId))
            return Forbid();

        InitiatePaymentResult result;
        try
        {
            result = await initiateHandler.HandleAsync(
                new InitiatePaymentCommand(paymentRef, order.CustomerNumber, order.FromBsb, order.FromAccountNumber,
                    order.PayeeName, order.ToBsb, order.ToAccountNumber, order.Amount, order.Reference, branch, userId),
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

        var payment = await queries.GetPaymentAsync(result.PaymentId, branch, cancellationToken);
        if (payment is null)
            return NotFound();

        Response.Headers.Location = $"/v2/PaymentOrderInitiation/{result.PaymentId}/Retrieve";
        return new JsonResult(PaymentOrderInitiationMapper.ToTransaction(payment), BianJson.Options)
        {
            StatusCode = result.Created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK
        };
    }

    /// <summary>BIAN Retrieve: the payment order. Outside the caller's scope it does not exist (as in /v1).</summary>
    [HttpGet("{paymentorderinitiationid:long}/Retrieve")]
    [Authorize(Policy = "PaymentView")]
    public async Task<IActionResult> Retrieve(long paymentorderinitiationid, CancellationToken cancellationToken)
    {
        var (allowed, branch) = PaymentsStaffAuthorizationHandler.ReadScopeOf(User, clientIds.AuditJourneyApi);
        if (!allowed)
            return Forbid();

        var payment = await queries.GetPaymentAsync(paymentorderinitiationid, branch, cancellationToken);
        return payment is null
            ? NotFound()
            : new JsonResult(PaymentOrderInitiationMapper.ToTransaction(payment), BianJson.Options);
    }

    /// <summary>BIAN Exchange on the OrderInitiation behaviour qualifier: a payments officer approves or rejects.</summary>
    [HttpPut("{paymentorderinitiationid:long}/OrderInitiation/{orderinitiationid}/Exchange")]
    [Authorize]
    public async Task<IActionResult> Exchange(
        long paymentorderinitiationid, string orderinitiationid, [FromBody] OrderInitiation? request, CancellationToken cancellationToken)
    {
        if (!string.Equals(orderinitiationid, PaymentOrderInitiationMapper.ApprovalStep, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var (decision, error) = PaymentOrderInitiationMapper.ToDecision(request);
        if (decision is null)
            return ValidationProblem(error!);

        // SEC: the same policy as /v1's approve or reject route (permission + MFA step-up) - chosen by the decision.
        var policy = decision.Approve ? "PaymentApprove" : "PaymentReject";
        if (!(await authorization.AuthorizeAsync(User, policy)).Succeeded)
            return Forbid();

        var branch = PaymentsStaffAuthorizationHandler.BranchOf(User);
        var userId = User.FindFirst("sub")?.Value;
        if (branch is null || string.IsNullOrWhiteSpace(userId))
            return Forbid();

        DecidePaymentResult? result;
        try
        {
            result = await decideHandler.HandleAsync(
                new DecidePaymentCommand(paymentorderinitiationid, decision.Approve, decision.Remarks, userId,
                    User.FindFirst("lan_id")?.Value ?? "a payments officer", PaymentsController.ClearanceOf(User), branch),
                cancellationToken);
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

        if (result is null)
            return NotFound();

        var payment = await queries.GetPaymentAsync(paymentorderinitiationid, branch, cancellationToken);
        return new JsonResult(
            PaymentOrderInitiationMapper.ToOrderInitiation(payment?.PaymentNumber ?? paymentorderinitiationid.ToString(), result.Status, decision.Approve),
            BianJson.Options);
    }
}