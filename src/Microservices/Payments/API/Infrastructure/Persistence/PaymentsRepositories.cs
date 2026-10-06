using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;

public sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByRefAsync(Guid paymentRef, CancellationToken cancellationToken) =>
        db.Payments.SingleOrDefaultAsync(x => x.PaymentRef == paymentRef, cancellationToken);

    public Task<Payment> GetAsync(long paymentId, CancellationToken cancellationToken) =>
        db.Payments.SingleAsync(x => x.Id == paymentId, cancellationToken);

    public void Add(Payment payment) => db.Payments.Add(payment);
}

public sealed class PaymentSagaRepository(PaymentsDbContext db) : IPaymentSagaRepository
{
    public async Task<PaymentSaga?> GetForUpdateAsync(Guid paymentRef, CancellationToken cancellationToken)
    {
        // Lock the row first: a reply and a due step for the same payment queue here.
        var ids = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM payment_sagas WHERE payment_ref = {paymentRef} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : await db.PaymentSagas.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public async Task<PaymentSaga?> GetNextDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SKIP LOCKED: several API instances run due steps in parallel without ever taking the same saga.
        var ids = await db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM payment_sagas
                WHERE status = 'RUNNING' AND next_check_at <= {now}
                ORDER BY next_check_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : await db.PaymentSagas.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public void Add(PaymentSaga saga) => db.PaymentSagas.Add(saga);
}

public sealed class PaymentsQueries(PaymentsDbContext db) : IPaymentsQueries
{
    public async Task<PagedResponse<PaymentSummary>> GetPaymentsAsync(
        BranchCode branch, int pageNumber, int pageSize, PaymentStatus? status, CancellationToken cancellationToken)
    {
        var query = db.Payments.AsNoTracking().Where(x => x.BranchCode == branch);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new PaymentSummary(
                x.Id,
                x.PaymentNumber,
                x.CustomerNumber,
                x.Amount,
                x.Currency,
                x.PayeeName,
                x.To.Bsb,
                x.To.AccountNumber,
                x.Status.ToCode(),
                x.ApprovalRequired,
                x.CreatedAt,
                x.UpdatedAt,
                x.InitiatedByUserId,
                db.StaffMembers.Where(m => m.UserId == x.InitiatedByUserId).Select(m => m.LanId).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResponse<PaymentSummary>(items, pageNumber, pageSize, total);
    }

    public async Task<PaymentDetail?> GetPaymentAsync(long paymentId, BranchCode branch, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking()
            .Where(x => x.Id == paymentId && x.BranchCode == branch)
            .SingleOrDefaultAsync(cancellationToken);
        if (payment is null)
            return null;

        var initiatorLanId = await db.StaffMembers.AsNoTracking()
            .Where(m => m.UserId == payment.InitiatedByUserId).Select(m => m.LanId).FirstOrDefaultAsync(cancellationToken);

        var saga = await db.PaymentSagas.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == paymentId, cancellationToken);
        SagaView? sagaView = null;
        if (saga is not null)
        {
            var timeline = await db.SagaHistory.AsNoTracking()
                .Where(h => h.SagaId == saga.Id)
                .OrderBy(h => h.Id)
                .Select(h => new SagaTimelineEntry(h.At, h.Step, h.Kind, h.Detail, h.MessageId))
                .ToListAsync(cancellationToken);

            sagaView = new SagaView(saga.Id, saga.Step.ToCode(), saga.Status.ToCode(), saga.Attempts, saga.NextCheckAt,
                saga.LastError, saga.WorkflowId, saga.CorrelationId, timeline);
        }

        return new PaymentDetail(
            payment.Id, payment.PaymentRef, payment.PaymentNumber, payment.CustomerNumber,
            payment.From.Bsb, payment.From.AccountNumber, payment.PayeeName, payment.To.Bsb, payment.To.AccountNumber,
            payment.Amount, payment.Currency, payment.Reference, payment.BranchCode.Value, payment.Status.ToCode(),
            payment.ApprovalRequired, payment.OutcomeCode, payment.OutcomeReason, payment.NetworkReference,
            payment.CreatedAt, payment.UpdatedAt, payment.EndedAt, payment.InitiatedByUserId, initiatorLanId, sagaView);
    }
}

/// <summary>A staff member this context has seen: subject ID (the identity) and LAN ID (the label).</summary>
public sealed class StaffMember
{
    public string UserId { get; set; } = null!;

    public string LanId { get; set; } = null!;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The context's own small staff directory (table staff_members), filled from the acting
/// person's token. Screens and published events show the LAN ID; records and every rule
/// keep the subject ID.
/// </summary>
public sealed class StaffDirectory(PaymentsDbContext db, TimeProvider clock) : IStaffDirectory
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