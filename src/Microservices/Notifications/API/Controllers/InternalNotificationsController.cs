using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Notifications.Api.Application;

namespace EnterpriseWebPlatform.Notifications.Api.Controllers;

/// <summary>A workflow event as consumed from Kafka: its identity, type and the full envelope.</summary>
public sealed record WorkflowEventRequest(Guid MessageId, string EventType, JsonElement Envelope);

/// <summary>
/// Machine-only endpoint: the Notifications Subscriber (pinned M2M client) hands each
/// workflow event here. Idempotent per MessageId (Inbox); the rules decide who is told.
/// </summary>
[ApiController]
[Route("internal/v1/notifications")]
public sealed class InternalNotificationsController(
    PublishFromEventHandler handler,
    ILogger<InternalNotificationsController> logger) : ControllerBase
{
    [HttpPost("events")]
    [Authorize(Policy = "NotificationsSubscriberWrite")]
    public async Task<IActionResult> FromEvent([FromBody] WorkflowEventRequest request, CancellationToken cancellationToken)
    {
        if (request.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.EventType) ||
            request.Envelope.ValueKind != JsonValueKind.Object)
            return ValidationProblem("MessageId, EventType and the event envelope are required.");

        var (outcome, count) = await handler.HandleAsync(request.MessageId, request.EventType.Trim(), request.Envelope, cancellationToken);

        logger.LogInformation("{EventType} {MessageId}: {Outcome} ({Count} notification(s)).",
            request.EventType, request.MessageId, outcome, count);

        return Ok(new { messageId = request.MessageId, result = outcome.ToString(), notifications = count });
    }
}