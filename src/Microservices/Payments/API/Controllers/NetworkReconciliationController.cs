using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>What the network's records show: SENT (with the network's reference for the payment) or NOT_SENT, and the evidence.</summary>
public sealed record ProposeNetworkResolutionRequest(string? Outcome, string? NetworkReference, string? Evidence);

/// <summary>The second person's remarks: optional to approve, required to reject.</summary>
public sealed record NetworkResolutionDecisionRequest(string? Remarks);

/// <summary>
/// Manual reconciliation of a payment the payment network never confirmed - only after the alert
/// time, and with four eyes: one operator proposes, ANOTHER approves or rejects. Each step needs
/// payment.reconcile and, with MFA on, a recent sign-in with the authenticator code.
/// </summary>
[ApiController]
[Route("v1/payments/{paymentId:long}/network-resolution")]
[Authorize(Policy = "PaymentReconcile")]
public sealed class NetworkReconciliationController(ReconcileNetworkOutcomeCommandHandler handler) : ControllerBase
{
    [HttpPost]
    public Task<IActionResult> Propose(long paymentId, [FromBody] ProposeNetworkResolutionRequest request, CancellationToken cancellationToken)
    {
        if (!PaymentsCodes.TryParseNetworkOutcome(request.Outcome, out var outcome))
            return Task.FromResult<IActionResult>(ValidationProblem("Outcome must be SENT or NOT_SENT."));

        return RunAsync(operatorId => handler.ProposeAsync(
            new ProposeNetworkResolutionCommand(paymentId, outcome, request.NetworkReference, request.Evidence ?? string.Empty,
                operatorId.UserId, operatorId.Label), cancellationToken));
    }

    [HttpPost("approve")]
    public Task<IActionResult> Approve(long paymentId, [FromBody] NetworkResolutionDecisionRequest? request, CancellationToken cancellationToken) =>
        RunAsync(operatorId => handler.DecideAsync(
            new DecideNetworkResolutionCommand(paymentId, Approve: true, request?.Remarks, operatorId.UserId, operatorId.Label), cancellationToken));

    [HttpPost("reject")]
    public Task<IActionResult> Reject(long paymentId, [FromBody] NetworkResolutionDecisionRequest? request, CancellationToken cancellationToken) =>
        RunAsync(operatorId => handler.DecideAsync(
            new DecideNetworkResolutionCommand(paymentId, Approve: false, request?.Remarks, operatorId.UserId, operatorId.Label), cancellationToken));

    private async Task<IActionResult> RunAsync(Func<(string UserId, string Label), Task<NetworkResolutionResult?>> run)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
            return Forbid();

        try
        {
            var result = await run((userId, User.FindFirst("lan_id")?.Value ?? "Operations"));
            return result is null ? NotFound() : Ok(new { status = result.Status, sagaStep = result.SagaStep });
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (SeparationOfDutiesViolationException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden, title: "Not permitted");
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Not possible now");
        }
    }
}