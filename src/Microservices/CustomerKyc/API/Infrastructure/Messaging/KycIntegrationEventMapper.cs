using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Common;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Events;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Messaging;

/// <summary>
/// Translates KYC domain events into the published integration events (Outbox rows).
/// The single place where internal domain language meets the external contract:
/// event-type names, status codes and workflow metadata are decided here.
/// </summary>
internal static class KycIntegrationEventMapper
{
    public const string AggregateType = "KycCase";
    public const string KycCaseCreated = "KycCaseCreated";

    private const string Source = "customer-kyc";

    /// <summary>Contract version of the published payloads (additive changes keep it).</summary>
    private const int SchemaVersion = 1;

    private const string IdentityApproved = "KycIdentityVerificationApproved";
    private const string DocumentApproved = "KycDocumentVerificationApproved";

    /// <summary>Returns null for internal domain events that have no published contract.</summary>
    public static async Task<OutboxMessage?> ToOutboxMessageAsync(
        KycDbContext db,
        KycCase kycCase,
        IDomainEvent domainEvent,
        Guid? workflowId,
        Guid? correlationId,
        Guid causationId,
        Guid? previousMessageId,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        string eventType;
        string payload;
        string? actedByUserId = null;

        switch (domainEvent)
        {
            case KycCaseOpenedDomainEvent:
                eventType = KycCaseCreated;
                payload = Envelope(messageId, eventType, domainEvent.OccurredAt, workflowId, correlationId, causationId, kycCase,
                    new KycCaseCreatedPayload(
                        kycCase.Id,
                        kycCase.ApplicationRef,
                        kycCase.ApplicationNumber,
                        kycCase.CustomerNumber,
                        kycCase.BranchCode.Value,
                        KycCaseStatus.PendingReview.ToCode()));
                break;

            case VerificationStageDecidedDomainEvent stage:
                eventType = StageEventType(stage.Stage, stage.NewStatus);
                actedByUserId = stage.DecidedByUserId;
                payload = Envelope(messageId, eventType, stage.OccurredAt, workflowId, correlationId, causationId, kycCase,
                    new KycVerificationStageDecisionPayload(
                        kycCase.Id,
                        kycCase.ApplicationRef,
                        kycCase.ApplicationNumber,
                        kycCase.CustomerNumber,
                        stage.Stage.ToCode(),
                        stage.PreviousStatus.ToCode(),
                        stage.NewStatus.ToCode(),
                        stage.DecidedByUserId,
                        stage.OccurredAt,
                        stage.Remarks,
                        stage.OverallStatus.ToCode()));
                break;

            case KycCaseDecidedDomainEvent decided:
                eventType = decided.NewStatus == KycCaseStatus.Approved ? "KycCaseApproved" : "KycCaseRejected";
                actedByUserId = decided.DecidedByUserId;
                var causedBy = await CausedByMessageIdsAsync(db, kycCase, decided, previousMessageId, cancellationToken);
                payload = Envelope(messageId, eventType, decided.OccurredAt, workflowId, correlationId, causationId, kycCase,
                    new KycCaseDecisionPayload(
                        kycCase.Id,
                        kycCase.ApplicationRef,
                        kycCase.ApplicationNumber,
                        kycCase.CustomerNumber,
                        decided.PreviousStatus.ToCode(),
                        decided.NewStatus.ToCode(),
                        decided.DecidedByUserId,
                        decided.OccurredAt,
                        decided.Remarks,
                        causedBy,
                        kycCase.BranchCode.Value,
                        kycCase.IdentityVerification.DecidedByUserId,
                        kycCase.DocumentVerification.DecidedByUserId,
                        new ApplicantPayload(
                            kycCase.Applicant.FirstName,
                            kycCase.Applicant.LastName,
                            new AddressPayload(
                                kycCase.Applicant.AddressLine1,
                                kycCase.Applicant.AddressLine2,
                                kycCase.Applicant.City,
                                kycCase.Applicant.State,
                                kycCase.Applicant.PostalCode,
                                kycCase.Applicant.CountryCode))));
                break;

            // Internal facts (ReBAC assignment) with no published contract. Listed
            // explicitly so that a NEW, unmapped domain event still fails loudly.
            case KycCaseAssignedDomainEvent:
            case KycCaseReleasedDomainEvent:
                return null;

            default:
                throw new InvalidOperationException(
                    $"No integration event mapping exists for domain event {domainEvent.GetType().Name}.");
        }

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = kycCase.Id.ToString(),
            EventType = eventType,
            Payload = payload,
            OccurredAt = domainEvent.OccurredAt,
            WorkflowId = workflowId,
            CorrelationId = correlationId,
            CausationId = causationId,
            InitiatedByUserId = kycCase.InitiatedByUserId,
            ActedByUserId = actedByUserId,
            // The request's trace; the relay continues it and sends it as a Kafka header.
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }

    private static string Envelope<TPayload>(
        Guid messageId,
        string eventType,
        DateTimeOffset occurredAt,
        Guid? workflowId,
        Guid? correlationId,
        Guid causationId,
        KycCase kycCase,
        TPayload payload) =>
        JsonSerializer.Serialize(new KycIntegrationEventEnvelope<TPayload>(
            messageId,
            eventType,
            SchemaVersion,
            Source,
            occurredAt,
            workflowId,
            correlationId,
            causationId,
            kycCase.InitiatedByUserId,
            payload));

    private static string StageEventType(VerificationStageType stage, VerificationStatus newStatus) =>
        (stage, newStatus) switch
        {
            (VerificationStageType.IdentityVerification, VerificationStatus.Approved) => IdentityApproved,
            (VerificationStageType.IdentityVerification, VerificationStatus.Rejected) => "KycIdentityVerificationRejected",
            (VerificationStageType.DocumentVerification, VerificationStatus.Approved) => DocumentApproved,
            (VerificationStageType.DocumentVerification, VerificationStatus.Rejected) => "KycDocumentVerificationRejected",
            _ => throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, "A stage event needs a decided status.")
        };

    /// <summary>
    /// The stage events that together caused the case decision: an approval is caused
    /// by BOTH stage approvals, a rejection by the rejecting stage only. The stage
    /// event raised by this same change is <paramref name="previousMessageId"/>.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> CausedByMessageIdsAsync(
        KycDbContext db,
        KycCase kycCase,
        KycCaseDecidedDomainEvent decided,
        Guid? previousMessageId,
        CancellationToken cancellationToken)
    {
        var causedBy = new List<Guid>();

        if (decided.NewStatus == KycCaseStatus.Approved)
        {
            var otherStageApproval = await db.OutboxMessages
                .AsNoTracking()
                .Where(x => x.AggregateType == AggregateType &&
                            x.AggregateId == kycCase.Id.ToString() &&
                            x.Id != previousMessageId &&
                            (x.EventType == IdentityApproved || x.EventType == DocumentApproved))
                .OrderByDescending(x => x.OccurredAt)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (otherStageApproval != Guid.Empty)
                causedBy.Add(otherStageApproval);
        }

        if (previousMessageId is { } stageMessageId)
            causedBy.Add(stageMessageId);

        return causedBy;
    }
}