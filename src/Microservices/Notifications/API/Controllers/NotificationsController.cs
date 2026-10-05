using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Notifications.Api.Application;
using EnterpriseWebPlatform.Notifications.Api.Domain;

namespace EnterpriseWebPlatform.Notifications.Api.Controllers;

/// <summary>
/// The signed-in person's notifications (called by the Shell BFF with their token).
/// Audiences come from the token only, so nobody can read another person's or branch's.
/// </summary>
[ApiController]
[Route("v1/notifications")]
public sealed class NotificationsController(NotificationQueries queries) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "NotificationsRead")]
    public async Task<ActionResult<NotificationsPage>> Get([FromQuery] bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        if (Me is not { } me)
            return Forbid();

        return Ok(await queries.GetAsync(me, NotificationAudiences.Of(User), unreadOnly, cancellationToken));
    }

    [HttpPost("{id:long}/read")]
    [Authorize(Policy = "NotificationsWrite")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken cancellationToken)
    {
        if (Me is not { } me)
            return Forbid();

        return await queries.MarkReadAsync(me, NotificationAudiences.Of(User), id, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("read-all")]
    [Authorize(Policy = "NotificationsWrite")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (Me is not { } me)
            return Forbid();

        var count = await queries.MarkAllReadAsync(me, NotificationAudiences.Of(User), cancellationToken);
        return Ok(new { marked = count });
    }

    // A person, never a machine: M2M tokens carry no "sub".
    private string? Me => User.FindFirst("sub")?.Value is { Length: > 0 } sub ? sub.Trim().ToLowerInvariant() : null;
}