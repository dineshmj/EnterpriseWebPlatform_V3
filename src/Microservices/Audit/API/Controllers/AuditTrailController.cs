using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Audit.Api.Application;
using EnterpriseWebPlatform.Audit.Api.Authorization;
using EnterpriseWebPlatform.Audit.Api.Domain;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;

namespace EnterpriseWebPlatform.Audit.Api.Controllers;

/// <summary>
/// The auditor's reads of the trail - callable only by the Audit Journey API acting for an
/// auditor (see <see cref="DelegatedAuditorRequirement"/>). Every read is itself recorded in
/// the trail as an ACCESS entry (who looked, at what, when): "who watched the watchers".
/// </summary>
[ApiController]
[Route("v1/audit")]
public sealed class AuditTrailController(
    AuditTrailQueries queries,
    AuditChainVerifier verifier,
    AuditTrailAppender appender,
    TimeProvider time) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet("entries")]
    [Authorize(Policy = AuditPolicies.Search)]
    public async Task<IActionResult> Search(
        [FromQuery] string? record, [FromQuery] string? person, [FromQuery] string? eventType,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? kind,
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize < 1 || pageSize > MaxPageSize)
            return ValidationProblem($"pageNumber must be at least 1 and pageSize between 1 and {MaxPageSize}.");
        var entryKind = string.IsNullOrWhiteSpace(kind) ? AuditEntryKind.Event : kind.Trim().ToUpperInvariant();
        if (entryKind is not (AuditEntryKind.Event or AuditEntryKind.Access))
            return ValidationProblem("kind must be EVENT or ACCESS.");
        if (new[] { record, person, eventType }.Any(v => v is { Length: > 100 }))
            return ValidationProblem("Search values are limited to 100 characters.");

        var result = await queries.SearchAsync(
            new AuditSearch(record, person, eventType, from, to, entryKind, pageNumber, pageSize), cancellationToken);

        await RecordAccessAsync("AuditTrailSearched", record,
            Describe(("person", person), ("type", eventType), ("kind", entryKind == AuditEntryKind.Event ? null : entryKind),
                     ("from", from?.ToString("u")), ("to", to?.ToString("u"))),
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("records/{recordRef}/timeline")]
    [Authorize(Policy = AuditPolicies.View)]
    public async Task<IActionResult> Timeline(string recordRef, CancellationToken cancellationToken)
    {
        if (recordRef.Length > 100)
            return NotFound();

        var entries = await queries.TimelineAsync(recordRef, cancellationToken);
        await RecordAccessAsync("AuditRecordViewed", recordRef, null, cancellationToken);
        return entries.Count == 0 ? NotFound() : Ok(entries);
    }

    /// <summary>Re-walks the whole chain now (the scheduled check also runs on its own).</summary>
    [HttpGet("integrity")]
    [Authorize(Policy = AuditPolicies.View)]
    public async Task<IActionResult> Integrity(CancellationToken cancellationToken)
    {
        var result = await verifier.VerifyAsync(cancellationToken);
        await RecordAccessAsync("AuditIntegrityVerified", null, result.Intact ? "INTACT" : $"BROKEN at {result.BrokenAtSequence}", cancellationToken);
        return Ok(result);
    }

    private Task RecordAccessAsync(string eventType, string? recordRef, string? detail, CancellationToken cancellationToken)
    {
        var now = AuditChain.ToStoredPrecision(time.GetUtcNow());
        return appender.AppendAsync(new AuditEntry
        {
            MessageId = Guid.CreateVersion7(),
            EntryKind = AuditEntryKind.Access,
            EventType = eventType,
            Source = "audit",
            OccurredAt = now,
            RecordedAt = now,
            ActorUserId = User.FindFirstValue("sub"),
            ActorLanId = User.FindFirstValue("lan_id"),
            RecordRef = recordRef is { Length: > 100 } ? recordRef[..100] : recordRef,
            Status = detail is { Length: > 100 } ? detail[..100] : detail
        }, cancellationToken);
    }

    private static string? Describe(params (string Name, string? Value)[] parts)
    {
        var text = string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Name}={p.Value}"));
        return text.Length == 0 ? null : text;
    }
}

public static class AuditPolicies
{
    public const string View = "AuditView";
    public const string Search = "AuditSearch";
}