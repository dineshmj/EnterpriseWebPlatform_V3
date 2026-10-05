using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;

/// <summary>A staff member this context has seen: subject ID (the identity) and LAN ID (the label).</summary>
public sealed class StaffMember
{
    public string UserId { get; set; } = null!;

    public string LanId { get; set; } = null!;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The context's own small staff directory (table staff_members), filled from the acting
/// officer's token and from the people events name. Screens and published events show
/// the LAN ID; records and every rule keep the subject ID.
/// </summary>
public sealed class StaffDirectory(AccountsDbContext db, TimeProvider clock) : IStaffDirectory
{
    // Mappings already written by this process: an unchanged LAN ID costs no database write.
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

    public async Task RememberAsync(string? userId, string? lanId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(lanId))
            return;

        var id = userId.Trim();
        var lan = lanId.Trim().ToLowerInvariant();
        if (lan.Length > 20 || (Known.TryGetValue(id, out var known) && known == lan))
            return;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO staff_members (user_id, lan_id, updated_at)
            VALUES ({id}, {lan}, {clock.GetUtcNow()})
            ON CONFLICT (user_id) DO UPDATE
                SET lan_id = EXCLUDED.lan_id, updated_at = EXCLUDED.updated_at
                WHERE staff_members.lan_id <> EXCLUDED.lan_id
            """, cancellationToken);

        Known[id] = lan;
    }
}