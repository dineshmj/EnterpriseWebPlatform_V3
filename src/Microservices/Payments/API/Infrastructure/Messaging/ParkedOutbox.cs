using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.Messaging;

/// <summary>
/// The Outbox rows the relay PARKED (attempt_count reached Payments:Outbox:MaxAttempts and
/// still unpublished). The list never carries the payload (account numbers): operations see
/// what the message is, for which payment, and why it failed.
/// </summary>
public sealed class ParkedOutbox(PaymentsDbContext db, OutboxPolicy policy) : IParkedOutbox
{
    private const int MaxListed = 200;

    public async Task<IReadOnlyList<ParkedOutboxMessage>> ListAsync(CancellationToken cancellationToken) =>
        await QueryAsync(null, lockRow: false, cancellationToken);

    public async Task<string?> FindAggregateIdAsync(Guid messageId, CancellationToken cancellationToken) =>
        await db.OutboxMessages
            .Where(x => x.Id == messageId)
            .Select(x => x.AggregateId)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ParkedOutboxMessage?> LockParkedAsync(Guid messageId, CancellationToken cancellationToken) =>
        (await QueryAsync(messageId, lockRow: true, cancellationToken)).SingleOrDefault();

    public async Task ResendAsync(Guid messageId, string byLanId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.SingleAsync(x => x.Id == messageId, cancellationToken);

        // Fresh attempts for the SAME message: the relay picks it up on its next cycle.
        message.AttemptCount = 0;
        message.LastAttemptAt = null;
        message.RedriveCount++;
        message.LastRedrivenAt = now;
        message.LastRedrivenBy = byLanId.Length > 200 ? byLanId[..200] : byLanId;
        message.LastRedriveReason = reason;
    }

    private Task<List<ParkedOutboxMessage>> QueryAsync(Guid? messageId, bool lockRow, CancellationToken cancellationToken)
    {
        var maxAttempts = policy.MaxAttempts;
        var query = lockRow
            ? db.Database.SqlQuery<ParkedOutboxMessage>($"""
                SELECT o.id AS "MessageId", o.event_type AS "EventType", o.aggregate_id AS "AggregateId",
                       p.payment_number AS "PaymentNumber", p.id AS "PaymentId",
                       o.occurred_at AS "OccurredAt", o.attempt_count AS "AttemptCount",
                       o.last_attempt_at AS "LastAttemptAt", o.last_error AS "LastError", 0 AS "HeldBack",
                       o.redrive_count AS "RedriveCount", o.last_redriven_at AS "LastRedrivenAt",
                       o.last_redriven_by AS "LastRedrivenBy", o.last_redrive_reason AS "LastRedriveReason"
                FROM outbox_messages o
                LEFT JOIN payments p ON p.payment_ref::text = o.aggregate_id
                WHERE o.id = {messageId} AND o.published_at IS NULL AND o.attempt_count >= {maxAttempts}
                FOR UPDATE OF o
                """)
            : db.Database.SqlQuery<ParkedOutboxMessage>($"""
                SELECT o.id AS "MessageId", o.event_type AS "EventType", o.aggregate_id AS "AggregateId",
                       p.payment_number AS "PaymentNumber", p.id AS "PaymentId",
                       o.occurred_at AS "OccurredAt", o.attempt_count AS "AttemptCount",
                       o.last_attempt_at AS "LastAttemptAt", o.last_error AS "LastError",
                       (SELECT count(*)::int FROM outbox_messages later
                        WHERE later.aggregate_type = o.aggregate_type AND later.aggregate_id = o.aggregate_id
                          AND later.published_at IS NULL AND later.sequence > o.sequence) AS "HeldBack",
                       o.redrive_count AS "RedriveCount", o.last_redriven_at AS "LastRedrivenAt",
                       o.last_redriven_by AS "LastRedrivenBy", o.last_redrive_reason AS "LastRedriveReason"
                FROM outbox_messages o
                LEFT JOIN payments p ON p.payment_ref::text = o.aggregate_id
                WHERE o.published_at IS NULL AND o.attempt_count >= {maxAttempts}
                ORDER BY o.sequence
                LIMIT {MaxListed}
                """);
        return query.ToListAsync(cancellationToken);
    }
}