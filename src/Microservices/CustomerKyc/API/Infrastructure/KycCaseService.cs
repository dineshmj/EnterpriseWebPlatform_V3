using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycCaseService(KycDbContext db, ILogger<KycCaseService> logger)
{
    public async Task<KycCaseResult> CreateOrGetAsync(CreateKycCaseRequest request, CancellationToken ct)
    {
        var existing = await db.KycCases.SingleOrDefaultAsync(x => x.CustomerNumber == request.CustomerNumber, ct);
        if (existing is not null)
            return new(existing, false);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            existing = await db.KycCases.SingleOrDefaultAsync(x => x.CustomerNumber == request.CustomerNumber, ct);
            if (existing is not null)
            {
                await transaction.RollbackAsync(ct);
                return new(existing, false);
            }

            var entity = new KycCase(request.CustomerNumber, "PENDING_REVIEW", request.InitiatedByUserId);
            db.KycCases.Add(entity);
            await db.SaveChangesAsync(ct);

            var payload = JsonSerializer.Serialize(new KycCaseCreatedEvent(
                Guid.NewGuid(), "KycCaseCreated", DateTimeOffset.UtcNow, entity.Id,
                entity.CustomerNumber, entity.Status, entity.InitiatedByUserId));

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), AggregateType = "KycCase", AggregateId = entity.Id.ToString(),
                EventType = "KycCaseCreated", Payload = payload, OccurredAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new(entity, true);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            existing = await db.KycCases.SingleOrDefaultAsync(x => x.CustomerNumber == request.CustomerNumber, ct);
            if (existing is not null) return new(existing, false);
            throw;
        }
    }
}

public sealed record CreateKycCaseRequest(string CustomerNumber, string? InitiatedByUserId);
public sealed record KycCaseResult(KycCase Case, bool Created);
public sealed record KycCaseCreatedEvent(Guid MessageId, string EventType, DateTimeOffset OccurredAt, long KycCaseId, string CustomerNumber, string Status, string? InitiatedByUserId);