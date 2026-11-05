using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;

/// <summary>An erasure request as operations see it: identifiers, people (LAN IDs) and outcome - no personal data.</summary>
public sealed record PersonalDataErasureRequestDto(
    long ErasureRequestId,
    string CustomerNumber,
    // CUSTOMER_REQUEST / RETENTION_ENDED
    string ReasonCode,
    // PENDING / ERASED / REJECTED / EXPIRED (a PENDING request past its expiry reads EXPIRED)
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionRemarks,
    // False for the requester: the screen does not offer them a decision (the API refuses it anyway).
    bool MayDecide);

/// <summary>The waiting requests first (oldest first), then the latest decided ones.</summary>
public sealed class GetPersonalDataErasureRequestsQueryHandler(
    IPersonalDataErasureReadContext readContext,
    IStaffDirectory staff,
    TimeProvider clock)
{
    private const int MaxDecided = 50;

    public async Task<IReadOnlyList<PersonalDataErasureRequestDto>> HandleAsync(string callerUserId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var pending = await readContext.PersonalDataErasureRequests
            .AsNoTracking()
            .Where(x => x.Status == PersonalDataErasureStatus.Pending)
            .OrderBy(x => x.Id)   // IDs rise with each request: oldest first
            .ToListAsync(cancellationToken);

        var decided = await readContext.PersonalDataErasureRequests
            .AsNoTracking()
            .Where(x => x.Status != PersonalDataErasureStatus.Pending)
            .OrderByDescending(x => x.Id)
            .Take(MaxDecided)
            .ToListAsync(cancellationToken);

        var rows = pending.Concat(decided).ToList();
        var people = await staff.LanIdsOfAsync(
            [.. rows.Select(x => x.RequestedByUserId).Concat(rows.Select(x => x.DecidedByUserId).OfType<string>()).Distinct()],
            cancellationToken);
        string Label(string userId) => people.TryGetValue(userId, out var lanId) ? lanId : "unknown";

        return [.. rows.Select(x =>
        {
            var expired = x.HasExpired(now);
            var status = expired ? PersonalDataErasureStatus.Expired : x.Status;
            return new PersonalDataErasureRequestDto(
                x.Id,
                x.CustomerNumber,
                x.Reason.ToCode(),
                status.ToString().ToUpperInvariant(),
                Label(x.RequestedByUserId),
                x.RequestedAt,
                x.ExpiresAt,
                x.DecidedByUserId is null ? null : Label(x.DecidedByUserId),
                x.DecidedAt,
                x.DecisionRemarks,
                status == PersonalDataErasureStatus.Pending &&
                    !string.Equals(x.RequestedByUserId, callerUserId, StringComparison.OrdinalIgnoreCase));
        })];
    }
}