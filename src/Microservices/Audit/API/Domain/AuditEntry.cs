using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseWebPlatform.Audit.Api.Domain;

/// <summary>What an entry records: a business fact from Kafka, or a person reading the trail.</summary>
public static class AuditEntryKind
{
    public const string Event = "EVENT";
    public const string Access = "ACCESS";
}

/// <summary>
/// One entry of the audit trail: who did what, to which record, when - and nothing more.
/// No names, addresses or payload copies are kept (data minimisation); the original
/// message is represented by its SHA-256 only, which still proves what was received.
/// Entries are appended, never changed: <see cref="Sequence"/>, <see cref="PreviousHash"/>
/// and <see cref="EntryHash"/> chain each entry to the one before (<see cref="AuditChain"/>).
/// </summary>
public sealed class AuditEntry
{
    public long Sequence { get; set; }
    public Guid MessageId { get; set; }
    public string EntryKind { get; set; } = AuditEntryKind.Event;
    public string EventType { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Topic { get; set; }
    public int? KafkaPartition { get; set; }
    public long? KafkaOffset { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? WorkflowId { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? InitiatedByUserId { get; set; }
    public string? InitiatedByLanId { get; set; }
    public string? ActorUserId { get; set; }
    public string? ActorLanId { get; set; }
    public string? RecordType { get; set; }
    public string? RecordRef { get; set; }
    public string? CustomerNumber { get; set; }
    public string? BranchCode { get; set; }
    public string? Status { get; set; }
    public string? ReasonCode { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? PayloadSha256 { get; set; }
    public string PreviousHash { get; set; } = AuditChain.Genesis;
    public string EntryHash { get; set; } = string.Empty;
}

/// <summary>
/// The tamper-evident hash chain. Each entry's hash covers its own content AND the previous
/// entry's hash, so changing, deleting or reordering any entry breaks every hash after it.
/// The database refuses updates and deletes anyway (trigger, and an INSERT/SELECT-only user);
/// the chain is what still catches a change made by someone who bypasses both.
/// </summary>
public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Version = "ewp-audit-v1";

    /// <summary>PostgreSQL keeps microseconds; hashing the same precision lets a stored entry be re-verified.</summary>
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static string ComputeHash(AuditEntry e)
    {
        // A JSON array is an unambiguous encoding: no separator can be forged by a field's content.
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            Version, e.Sequence, e.PreviousHash, e.MessageId, e.EntryKind, e.EventType, e.Source,
            e.Topic, e.KafkaPartition, e.KafkaOffset, Timestamp(e.OccurredAt), Timestamp(e.RecordedAt),
            e.WorkflowId, e.CorrelationId, e.CausationId, e.InitiatedByUserId, e.InitiatedByLanId,
            e.ActorUserId, e.ActorLanId, e.RecordType, e.RecordRef, e.CustomerNumber, e.BranchCode,
            e.Status, e.ReasonCode, e.Amount?.ToString("0.00", CultureInfo.InvariantCulture), e.Currency,
            e.PayloadSha256
        });
        return Sha256Hex(Encoding.UTF8.GetBytes(canonical));
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Timestamp(DateTimeOffset value) =>
        ToStoredPrecision(value).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}