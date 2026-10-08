using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Audit.Api.Domain;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;

namespace EnterpriseWebPlatform.Audit.Api.Application;

public sealed record PersonView(string UserId, string? LanId);

public sealed record AuditEntryView(
    long Sequence,
    string Kind,
    string EventType,
    string? Source,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? RecordType,
    string? RecordRef,
    string? CustomerNumber,
    string? BranchCode,
    string? Status,
    string? ReasonCode,
    decimal? Amount,
    string? Currency,
    PersonView? InitiatedBy,
    PersonView? Actor,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? PayloadSha256,
    string EntryHash);

public sealed record AuditSearch(
    string? Record,
    string? Person,
    string? EventType,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string Kind,
    int PageNumber,
    int PageSize);

public sealed record AuditPage(IReadOnlyList<AuditEntryView> Items, int PageNumber, int PageSize, long TotalCount);

/// <summary>
/// Reads of the trail. People are shown by LAN ID, resolved from the trail itself (every
/// subject ID / LAN ID pair it has seen), so an entry whose producer sent only the subject ID
/// - e.g. Accounts' funds replies - still shows who initiated it.
/// </summary>
public sealed class AuditTrailQueries(AuditDbContext db)
{
    public async Task<AuditPage> SearchAsync(AuditSearch search, CancellationToken cancellationToken)
    {
        var query = db.Entries.AsNoTracking().Where(e => e.EntryKind == search.Kind);

        if (!string.IsNullOrWhiteSpace(search.Record))
        {
            var record = search.Record.Trim();
            query = query.Where(e => e.RecordRef == record || e.CustomerNumber == record);
        }

        if (!string.IsNullOrWhiteSpace(search.Person))
        {
            // A LAN ID or a subject ID; a LAN ID also finds entries that carry only the subject ID.
            var person = search.Person.Trim();
            var subjects = await SubjectsOfAsync(person, cancellationToken);
            subjects.Add(person);
            query = query.Where(e =>
                (e.InitiatedByUserId != null && subjects.Contains(e.InitiatedByUserId)) ||
                (e.ActorUserId != null && subjects.Contains(e.ActorUserId)) ||
                e.InitiatedByLanId == person || e.ActorLanId == person);
        }

        if (!string.IsNullOrWhiteSpace(search.EventType))
            query = query.Where(e => e.EventType == search.EventType.Trim());
        if (search.From is { } from)
            query = query.Where(e => e.OccurredAt >= from);
        if (search.To is { } to)
            query = query.Where(e => e.OccurredAt < to);

        var total = await query.LongCountAsync(cancellationToken);
        var page = await query
            .OrderByDescending(e => e.Sequence)
            .Skip((search.PageNumber - 1) * search.PageSize)
            .Take(search.PageSize)
            .ToListAsync(cancellationToken);

        return new AuditPage(await ToViewsAsync(page, cancellationToken), search.PageNumber, search.PageSize, total);
    }

    /// <summary>
    /// One record's story, oldest first: every entry for the record, plus every entry of the
    /// same workflow(s) - e.g. a payment's funds replies, an onboarding's KYC stage decisions.
    /// </summary>
    public async Task<IReadOnlyList<AuditEntryView>> TimelineAsync(string recordRef, CancellationToken cancellationToken)
    {
        var workflows = await db.Entries.AsNoTracking()
            .Where(e => e.EntryKind == AuditEntryKind.Event && e.RecordRef == recordRef && e.WorkflowId != null)
            .Select(e => e.WorkflowId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.EntryKind == AuditEntryKind.Event &&
                        (e.RecordRef == recordRef || (e.WorkflowId != null && workflows.Contains(e.WorkflowId.Value))))
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Sequence)
            .Take(500)
            .ToListAsync(cancellationToken);

        return await ToViewsAsync(entries, cancellationToken);
    }

    private async Task<List<string>> SubjectsOfAsync(string lanId, CancellationToken cancellationToken) =>
        await db.Entries.AsNoTracking()
            .Where(e => e.InitiatedByLanId == lanId && e.InitiatedByUserId != null).Select(e => e.InitiatedByUserId!)
            .Union(db.Entries.AsNoTracking().Where(e => e.ActorLanId == lanId && e.ActorUserId != null).Select(e => e.ActorUserId!))
            .Distinct()
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<AuditEntryView>> ToViewsAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        var people = entries.SelectMany(e => new[] { e.InitiatedByUserId, e.ActorUserId }).OfType<string>().Distinct().ToList();
        var lanIds = (await db.Entries.AsNoTracking()
                .Where(e => e.InitiatedByUserId != null && e.InitiatedByLanId != null && people.Contains(e.InitiatedByUserId))
                .Select(e => new { UserId = e.InitiatedByUserId!, LanId = e.InitiatedByLanId! })
                .Union(db.Entries.AsNoTracking()
                    .Where(e => e.ActorUserId != null && e.ActorLanId != null && people.Contains(e.ActorUserId))
                    .Select(e => new { UserId = e.ActorUserId!, LanId = e.ActorLanId! }))
                .Distinct()
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.First().LanId);

        PersonView? Person(string? userId, string? lanId) =>
            userId is null ? null : new PersonView(userId, lanId ?? lanIds.GetValueOrDefault(userId));

        return entries.Select(e => new AuditEntryView(
            e.Sequence, e.EntryKind, e.EventType, e.Source, e.OccurredAt, e.RecordedAt, e.RecordType, e.RecordRef,
            e.CustomerNumber, e.BranchCode, e.Status, e.ReasonCode, e.Amount, e.Currency,
            Person(e.InitiatedByUserId, e.InitiatedByLanId), Person(e.ActorUserId, e.ActorLanId),
            e.WorkflowId, e.CorrelationId, e.CausationId, e.PayloadSha256, e.EntryHash)).ToList();
    }
}