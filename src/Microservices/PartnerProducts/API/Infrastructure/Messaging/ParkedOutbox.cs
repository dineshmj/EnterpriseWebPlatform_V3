using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Messaging;

/// <summary>A message the Outbox relay PARKED after its bounded attempts - no payload (it may hold personal data).</summary>
public sealed record ParkedOutboxMessage(
    Guid MessageId,
    string EventType,
    string AggregateType,
    string AggregateId,
    DateTimeOffset OccurredAt,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    int HeldBack,
    int RedriveCount,
    DateTimeOffset? LastRedrivenAt,
    string? LastRedrivenBy,
    string? LastRedriveReason);

public sealed record ResentOutboxMessage(Guid MessageId, string EventType, int RedriveCount);

// ┌─ :: ARCHITECTURE + SECURITY :: Resend a parked Outbox message ─────────────────────────────────
// │ Why : A message parked after Kafka was down for long blocks the later messages of its Form VII
// │       or purchase for good; fixing it by hand in the database would be unrecorded and could
// │       duplicate it.
// │ How : The same tool as Accounts: one atomic UPDATE hands the SAME message (same ID) back to the
// │       relay with fresh attempts, only if it is still parked; who, when and why are kept on the
// │       row. Consumers' Inboxes drop a copy that did arrive. Operators reach it through the
// │       Operations Journey API only.
// │ Ref : Features 1.6.2 | Threat-Model R-5 | Transactional Outbox pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ParkedOutbox(PartnerProductsDbContext db)
{
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 500;
    private const int MaxListed = 200;

    public async Task<IReadOnlyList<ParkedOutboxMessage>> ListAsync(CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<ParkedOutboxMessage>($"""
            SELECT o.id AS "MessageId", o.event_type AS "EventType", o.aggregate_type AS "AggregateType",
                   o.aggregate_id AS "AggregateId", o.occurred_at AS "OccurredAt", o.attempt_count AS "AttemptCount",
                   o.last_attempt_at AS "LastAttemptAt", o.last_error AS "LastError",
                   (SELECT count(*)::int FROM outbox_messages later
                    WHERE later.aggregate_type = o.aggregate_type AND later.aggregate_id = o.aggregate_id
                      AND later.published_at IS NULL AND later.sequence > o.sequence) AS "HeldBack",
                   o.redrive_count AS "RedriveCount", o.last_redriven_at AS "LastRedrivenAt",
                   o.last_redriven_by AS "LastRedrivenBy", o.last_redrive_reason AS "LastRedriveReason"
            FROM outbox_messages o
            WHERE o.published_at IS NULL AND o.attempt_count >= {PartnerProductsOutboxPublisher.MaxAttempts}
            ORDER BY o.sequence
            LIMIT {MaxListed}
            """).ToListAsync(cancellationToken);

    /// <summary>
    /// Null when the message does not exist; throws <see cref="InvalidOperationException"/> when it is
    /// not parked (still being tried, or already sent).
    /// </summary>
    public async Task<ResentOutboxMessage?> ResendAsync(Guid messageId, string byLanId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var by = byLanId.Length > 200 ? byLanId[..200] : byLanId;

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Only a message that is STILL parked is resent - checked and changed in one statement,
        // │ so two operators (or the relay) racing on it cannot resend it twice.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var resent = (await db.Database.SqlQuery<ResentOutboxMessage>($"""
            UPDATE outbox_messages
            SET attempt_count = 0, last_attempt_at = NULL, redrive_count = redrive_count + 1,
                last_redriven_at = {now}, last_redriven_by = {by}, last_redrive_reason = {reason}
            WHERE id = {messageId} AND published_at IS NULL AND attempt_count >= {PartnerProductsOutboxPublisher.MaxAttempts}
            RETURNING id AS "MessageId", event_type AS "EventType", redrive_count AS "RedriveCount"
            """).ToListAsync(cancellationToken)).SingleOrDefault();
        if (resent is not null)
            return resent;

        var exists = await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM outbox_messages WHERE id = {messageId}")
            .AnyAsync(cancellationToken);
        return exists
            ? throw new InvalidOperationException("The message is not parked: the relay is still trying it, or it was already sent.")
            : null;
    }
}