using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Duende.IdentityServer.Services;

using EnterpriseWebPlatform.IdentityServer.Data;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY + ARCHITECTURE :: One-time proofs, shared by every IDP instance ─────────────────
// │ Why : A DPoP proof (RFC 9449 §11.1) and a private_key_jwt client assertion (RFC 7523) may be
// │       used ONCE: their jti is remembered until the proof expires. Remembered in one instance's
// │       memory, the same proof sent to another instance - several pods behind one address -
// │       would be accepted again within its lifetime.
// │ How : Duende's IReplayCache backed by the IDP's own PostgreSQL database (schema
// │       identity_server, like its operational store and key ring): no extra process, no cloud
// │       service, the same in every environment. Only a SHA-256 of the handle is kept. Adding
// │       is an upsert; a lookup counts only rows not yet expired; expired rows are deleted when
// │       new ones are added.
// │       Check-then-add is Duende's own sequence (as with its distributed-cache version): two
// │       copies of one proof arriving at the same instant could both pass - a window of
// │       milliseconds, not of the proof's lifetime.
// │ Ref : Features 2.1.6 | RFC 9449 §11.1 | RFC 7523 §3 | Threat-Model R-14
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Duende's replay cache in the IDP's database, so every instance sees every used proof.</summary>
public sealed class DatabaseReplayCache(IdentityDbContext db, TimeProvider time) : IReplayCache
{
    public async Task AddAsync(string purpose, string handle, DateTimeOffset expiration, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var key = HandleKey(handle);
        var expiresAt = expiration.ToUniversalTime();

        // Housekeeping: an expired entry protects nothing.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM identity_server.replay_cache WHERE expires_at < {now}", ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO identity_server.replay_cache (purpose, handle_hash, expires_at)
            VALUES ({purpose}, {key}, {expiresAt})
            ON CONFLICT (purpose, handle_hash)
            DO UPDATE SET expires_at = GREATEST(identity_server.replay_cache.expires_at, EXCLUDED.expires_at)
            """, ct);
    }

    public async Task<bool> ExistsAsync(string purpose, string handle, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var key = HandleKey(handle);

        return await db.Database
            .SqlQuery<int>(
                $"""
                SELECT 1 AS "Value" FROM identity_server.replay_cache
                WHERE purpose = {purpose} AND handle_hash = {key} AND expires_at > {now}
                """)
            .AnyAsync(ct);
    }

    // SEC: the handle is the proof's jti - kept only as a hash, at a fixed length.
    private static string HandleKey(string handle) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
}