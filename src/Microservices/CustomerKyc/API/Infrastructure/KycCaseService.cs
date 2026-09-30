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

            var messageId = Guid.NewGuid();
            var occurredAt = DateTimeOffset.UtcNow;

            var payload = JsonSerializer.Serialize(new KycCaseCreatedEvent(
                messageId,
                "KycCaseCreated",
                occurredAt,
                request.WorkflowId,
                request.CorrelationId,
                request.CausationId,
                entity.Id,
                entity.CustomerNumber,
                entity.Status,
                entity.InitiatedByUserId));

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                AggregateType = "KycCase",
                AggregateId = entity.Id.ToString(),
                EventType = "KycCaseCreated",
                Payload = payload,
                OccurredAt = occurredAt,
                WorkflowId = request.WorkflowId,
                CorrelationId = request.CorrelationId,
                CausationId = request.CausationId
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Created KYC case {KycCaseId} for CustomerNumber={CustomerNumber}. MessageId={MessageId}, WorkflowId={WorkflowId}, CorrelationId={CorrelationId}, CausationId={CausationId}",
                entity.Id, entity.CustomerNumber, messageId,
                request.WorkflowId, request.CorrelationId, request.CausationId);

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

    public async Task<KycCaseStageDecisionResult> DecideStageAsync(
        long caseId,
        KycVerificationStage stage,
        KycCaseDecision decision,
        string decisionByUserId,
        string? decisionRemarks,
        CancellationToken ct)
    {
        var remarks = string.IsNullOrWhiteSpace(decisionRemarks)
            ? null
            : decisionRemarks.Trim();

        if (remarks?.Length > 4000)
            return KycCaseStageDecisionResult.ValidationFailed(
                "Decision remarks must not exceed 4000 characters.");

        if (decision == KycCaseDecision.Reject && string.IsNullOrWhiteSpace(remarks))
            return KycCaseStageDecisionResult.ValidationFailed(
                "Decision remarks are required when rejecting a verification stage.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.KycCases
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == caseId, ct);

        if (existing is null)
        {
            await transaction.RollbackAsync(ct);
            return KycCaseStageDecisionResult.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(existing.InitiatedByUserId) &&
            string.Equals(existing.InitiatedByUserId, decisionByUserId, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            return KycCaseStageDecisionResult.Forbidden(
                "The workflow initiator cannot also approve or reject a KYC verification stage.");
        }

        if (existing.Status is "APPROVED" or "REJECTED")
        {
            await transaction.RollbackAsync(ct);
            return KycCaseStageDecisionResult.Conflict(
                "The KYC case has already reached a terminal state.");
        }

        var currentStageStatus = stage == KycVerificationStage.IdentityVerification
            ? existing.IdentityVerificationStatus
            : existing.DocumentVerificationStatus;

        if (!string.Equals(currentStageStatus, "PENDING_REVIEW", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(ct);
            return KycCaseStageDecisionResult.Conflict(
                $"The {GetStageDisplayName(stage)} stage is no longer awaiting review.");
        }

        var stageNewStatus = decision == KycCaseDecision.Approve ? "APPROVED" : "REJECTED";
        var decisionAt = DateTimeOffset.UtcNow;

        var otherStageStatus = stage == KycVerificationStage.IdentityVerification
            ? existing.DocumentVerificationStatus
            : existing.IdentityVerificationStatus;

        var overallNewStatus = decision == KycCaseDecision.Reject
            ? "REJECTED"
            : string.Equals(otherStageStatus, "APPROVED", StringComparison.Ordinal)
                ? "APPROVED"
                : "PENDING_REVIEW";

        // The WHERE clause makes the stage decision atomic. If two users act
        // on the same stage concurrently, exactly one can transition it.
        var affected = stage == KycVerificationStage.IdentityVerification
            ? await db.KycCases
                .Where(x => x.Id == caseId &&
                            x.Status == "PENDING_REVIEW" &&
                            x.IdentityVerificationStatus == "PENDING_REVIEW")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.IdentityVerificationStatus, stageNewStatus)
                    .SetProperty(x => x.IdentityVerificationByUserId, decisionByUserId)
                    .SetProperty(x => x.IdentityVerificationAt, decisionAt)
                    .SetProperty(x => x.IdentityVerificationRemarks, remarks)
                    .SetProperty(x => x.Status, overallNewStatus)
                    .SetProperty(x => x.DecisionByUserId,
                        overallNewStatus == "PENDING_REVIEW" ? null : decisionByUserId)
                    .SetProperty(x => x.DecisionAt,
                        overallNewStatus == "PENDING_REVIEW" ? null : decisionAt)
                    .SetProperty(x => x.DecisionRemarks,
                        overallNewStatus == "PENDING_REVIEW" ? null : remarks)
                    .SetProperty(x => x.UpdatedAt, decisionAt), ct)
            : await db.KycCases
                .Where(x => x.Id == caseId &&
                            x.Status == "PENDING_REVIEW" &&
                            x.DocumentVerificationStatus == "PENDING_REVIEW")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.DocumentVerificationStatus, stageNewStatus)
                    .SetProperty(x => x.DocumentVerificationByUserId, decisionByUserId)
                    .SetProperty(x => x.DocumentVerificationAt, decisionAt)
                    .SetProperty(x => x.DocumentVerificationRemarks, remarks)
                    .SetProperty(x => x.Status, overallNewStatus)
                    .SetProperty(x => x.DecisionByUserId,
                        overallNewStatus == "PENDING_REVIEW" ? null : decisionByUserId)
                    .SetProperty(x => x.DecisionAt,
                        overallNewStatus == "PENDING_REVIEW" ? null : decisionAt)
                    .SetProperty(x => x.DecisionRemarks,
                        overallNewStatus == "PENDING_REVIEW" ? null : remarks)
                    .SetProperty(x => x.UpdatedAt, decisionAt), ct);

        if (affected != 1)
        {
            await transaction.RollbackAsync(ct);
            return KycCaseStageDecisionResult.Conflict(
                "The verification stage is no longer awaiting review. Another decision may already have been recorded.");
        }

        var createdMessage = await db.OutboxMessages
            .AsNoTracking()
            .Where(x => x.AggregateType == "KycCase" &&
                        x.AggregateId == caseId.ToString() &&
                        x.EventType == "KycCaseCreated")
            .OrderBy(x => x.OccurredAt)
            .Select(x => new { x.WorkflowId, x.CorrelationId })
            .FirstOrDefaultAsync(ct);

        Guid? workflowId = createdMessage?.WorkflowId;
        Guid? correlationId = createdMessage?.CorrelationId;

        var stageEventType = GetStageEventType(stage, decision);
        var stageMessageId = Guid.NewGuid();
        var stageCausationId = Guid.NewGuid();

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = stageMessageId,
            AggregateType = "KycCase",
            AggregateId = caseId.ToString(),
            EventType = stageEventType,
            Payload = JsonSerializer.Serialize(new KycVerificationStageDecisionEvent(
                stageMessageId,
                stageEventType,
                decisionAt,
                workflowId,
                correlationId,
                stageCausationId,
                caseId,
                existing.CustomerNumber,
                stage,
                "PENDING_REVIEW",
                stageNewStatus,
                existing.InitiatedByUserId,
                decisionByUserId,
                decisionAt,
                remarks,
                overallNewStatus)),
            OccurredAt = decisionAt,
            WorkflowId = workflowId,
            CorrelationId = correlationId,
            CausationId = stageCausationId
        });

        if (overallNewStatus is "APPROVED" or "REJECTED")
        {
            var overallEventType = overallNewStatus == "APPROVED"
                ? "KycCaseApproved"
                : "KycCaseRejected";

            var overallMessageId = Guid.NewGuid();

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = overallMessageId,
                AggregateType = "KycCase",
                AggregateId = caseId.ToString(),
                EventType = overallEventType,
                Payload = JsonSerializer.Serialize(new KycCaseDecisionEvent(
                    overallMessageId,
                    overallEventType,
                    decisionAt,
                    workflowId,
                    correlationId,
                    stageMessageId,
                    caseId,
                    existing.CustomerNumber,
                    "PENDING_REVIEW",
                    overallNewStatus,
                    existing.InitiatedByUserId,
                    decisionByUserId,
                    decisionAt,
                    remarks)),
                OccurredAt = decisionAt,
                WorkflowId = workflowId,
                CorrelationId = correlationId,
                CausationId = stageMessageId
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "KYC case {KycCaseId} {Stage} changed from PENDING_REVIEW to {StageStatus} by {DecisionByUserId}. OverallStatus={OverallStatus}, MessageId={MessageId}, WorkflowId={WorkflowId}, CorrelationId={CorrelationId}, CausationId={CausationId}",
            caseId, stage, stageNewStatus, decisionByUserId, overallNewStatus,
            stageMessageId, workflowId, correlationId, stageCausationId);

        return KycCaseStageDecisionResult.Success(
            caseId, existing.CustomerNumber, stage, stageNewStatus, overallNewStatus,
            decisionByUserId, decisionAt, remarks);
    }

    private static string GetStageDisplayName(KycVerificationStage stage) =>
        stage == KycVerificationStage.IdentityVerification
            ? "identity verification"
            : "document verification";

    private static string GetStageEventType(
        KycVerificationStage stage,
        KycCaseDecision decision) =>
        (stage, decision) switch
        {
            (KycVerificationStage.IdentityVerification, KycCaseDecision.Approve)
                => "KycIdentityVerificationApproved",
            (KycVerificationStage.IdentityVerification, KycCaseDecision.Reject)
                => "KycIdentityVerificationRejected",
            (KycVerificationStage.DocumentVerification, KycCaseDecision.Approve)
                => "KycDocumentVerificationApproved",
            (KycVerificationStage.DocumentVerification, KycCaseDecision.Reject)
                => "KycDocumentVerificationRejected",
            _ => throw new ArgumentOutOfRangeException()
        };
}

public enum KycVerificationStage
{
    IdentityVerification,
    DocumentVerification
}

public enum KycCaseDecision
{
    Approve,
    Reject
}

public sealed record KycCaseDecisionRequest(string? DecisionRemarks);

public sealed record KycCaseDecisionResponse(
    long KycCaseId,
    string CustomerNumber,
    KycVerificationStage Stage,
    string StageStatus,
    string OverallStatus,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks);

public sealed record KycVerificationStageDecisionEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    string CustomerNumber,
    KycVerificationStage Stage,
    string PreviousStageStatus,
    string NewStageStatus,
    string? InitiatedByUserId,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks,
    string OverallStatus);

public sealed record KycCaseDecisionEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    string CustomerNumber,
    string PreviousStatus,
    string NewStatus,
    string? InitiatedByUserId,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks);

public sealed record KycCaseStageDecisionResult(
    bool Succeeded,
    bool IsNotFound,
    bool IsConflict,
    bool IsForbidden,
    bool IsValidationFailure,
    string? Error,
    long? KycCaseId,
    string? CustomerNumber,
    KycVerificationStage? Stage,
    string? StageStatus,
    string? OverallStatus,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks)
{
    public static KycCaseStageDecisionResult Success(
        long caseId,
        string customerNumber,
        KycVerificationStage stage,
        string stageStatus,
        string overallStatus,
        string decisionByUserId,
        DateTimeOffset decisionAt,
        string? remarks) =>
        new(true, false, false, false, false, null, caseId, customerNumber, stage,
            stageStatus, overallStatus, decisionByUserId, decisionAt, remarks);

    public static KycCaseStageDecisionResult NotFound() =>
        new(false, true, false, false, false, "KYC case was not found.",
            null, null, null, null, null, null, null, null);

    public static KycCaseStageDecisionResult Conflict(string error) =>
        new(false, false, true, false, false, error,
            null, null, null, null, null, null, null, null);

    public static KycCaseStageDecisionResult Forbidden(string error) =>
        new(false, false, false, true, false, error,
            null, null, null, null, null, null, null, null);

    public static KycCaseStageDecisionResult ValidationFailed(string error) =>
        new(false, false, false, false, true, error,
            null, null, null, null, null, null, null, null);
}

public sealed record CreateKycCaseRequest(
    string CustomerNumber,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId);

public sealed record KycCaseResult(KycCase Case, bool Created);

public sealed record KycCaseCreatedEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    string CustomerNumber,
    string Status,
    string? InitiatedByUserId);