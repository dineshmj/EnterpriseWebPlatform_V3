using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.Compliance.Api.Controllers;

/// <summary>Why operations resend a parked message: required, recorded on the message.</summary>
public sealed record ResendParkedMessageRequest(string? Reason);

/// <summary>
/// Operations recovery for the Compliance Outbox: the messages the relay PARKED, and the resend of one
/// of them. Reached only through the Operations Journey API, acting for an operator (any branch:
/// the operations desk is central).
/// </summary>
[ApiController]
[Route("v1/compliance/outbox")]
public sealed class OutboxController(ParkedOutbox outbox, TimeProvider clock, ILogger<OutboxController> logger) : ControllerBase
{
    [HttpGet("parked")]
    [Authorize(Policy = "ComplianceOutboxView")]
    public async Task<IActionResult> GetParked(CancellationToken cancellationToken) =>
        Ok(await outbox.ListAsync(cancellationToken));

    [HttpPost("{messageId:guid}/resend")]
    [Authorize(Policy = "ComplianceOutboxResend")]
    public async Task<IActionResult> Resend(Guid messageId, ResendParkedMessageRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the reason is missing or outside 10-500 characters, refuse: every resend must say
        // │ why, for the record.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (reason.Length is < ParkedOutbox.MinReasonLength or > ParkedOutbox.MaxReasonLength)
            return ValidationProblem($"A reason of {ParkedOutbox.MinReasonLength} to {ParkedOutbox.MaxReasonLength} characters is required.");

        var operatorLabel = User.FindFirst("lan_id")?.Value ?? "Operations";
        try
        {
            var result = await outbox.ResendAsync(messageId, operatorLabel, reason, clock.GetUtcNow(), cancellationToken);
            if (result is null)
                return NotFound();

            logger.LogWarning("Operations ({Operator}) resent parked Compliance outbox message {MessageId} {EventType}. Reason: {Reason}",
                operatorLabel, result.MessageId, result.EventType, reason);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to resend");
        }
    }
}