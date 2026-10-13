using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>Why operations resend a parked message: required, recorded on the payment's timeline.</summary>
public sealed record ResendParkedMessageRequest(string? Reason);

/// <summary>
/// Operations recovery for the Payments Outbox: the messages the relay PARKED after its
/// bounded attempts, and the resend of one of them. Any branch: the operations desk is central.
/// </summary>
[ApiController]
[Route("v1/payments/outbox")]
public sealed class OutboxController(
    IParkedOutbox outbox,
    ResendParkedMessageCommandHandler resendHandler) : ControllerBase
{
    [HttpGet("parked")]
    [Authorize(Policy = "PaymentOutboxView")]
    public async Task<IActionResult> GetParked(CancellationToken cancellationToken) =>
        Ok(await outbox.ListAsync(cancellationToken));

    /// <summary>
    /// The same message (same id) goes back to the relay with fresh attempts; consumers drop it
    /// if an earlier attempt did arrive. Needs a reason, workflow.reprocess and - with MFA on - a
    /// recent sign-in with the authenticator code.
    /// </summary>
    [HttpPost("{messageId:guid}/resend")]
    [Authorize(Policy = "PaymentOutboxResend")]
    public async Task<IActionResult> Resend(Guid messageId, ResendParkedMessageRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < ResendParkedMessageCommandHandler.MinReasonLength or > ResendParkedMessageCommandHandler.MaxReasonLength)
        {
            return ValidationProblem(
                $"A reason of {ResendParkedMessageCommandHandler.MinReasonLength} to {ResendParkedMessageCommandHandler.MaxReasonLength} characters is required.");
        }

        try
        {
            var result = await resendHandler.HandleAsync(
                new ResendParkedMessageCommand(messageId, User.FindFirst("lan_id")?.Value ?? "Operations", reason), cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to resend");
        }
    }
}