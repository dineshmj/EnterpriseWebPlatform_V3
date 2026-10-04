using System.Text.Json;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.Common;
using EnterpriseWebPlatform.Compliance.Api.Domain.Events;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Messaging;

// Published contracts (topics compliance.case.*), in the platform's standard envelope.

public sealed record ComplianceIntegrationEventEnvelope<TPayload>(
    Guid MessageId,
    string EventType,
    int SchemaVersion,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? InitiatedByUserId,
    TPayload Payload);

public sealed record ComplianceCaseCreatedPayload(
    long ComplianceCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    long KycCaseId,
    string Status);

public sealed record ComplianceCaseDecisionPayload(
    long ComplianceCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string PreviousStatus,
    string NewStatus,
    string? ScreeningOutcome,
    string? RiskRating,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks);

/// <summary>
/// Translates Compliance domain events into published integration events (Outbox
/// rows): the single place where the domain's language meets the public contract.
/// Screening, assignment and hold changes are internal and are not published.
/// </summary>
internal static class ComplianceIntegrationEventMapper
{
    public const string AggregateType = "ComplianceCase";
    public const string ComplianceCaseCreated = "ComplianceCaseCreated";
    public const string ComplianceCaseApproved = "ComplianceCaseApproved";
    public const string ComplianceCaseRejected = "ComplianceCaseRejected";

    private const string Source = "compliance";
    private const int SchemaVersion = 1;

    public static OutboxMessage? ToOutboxMessage(
        ComplianceCase complianceCase,
        IDomainEvent domainEvent,
        Guid? workflowId,
        Guid? correlationId,
        Guid causationId)
    {
        var messageId = Guid.NewGuid();
        string eventType;
        string payload;
        string? actedBy = null;

        switch (domainEvent)
        {
            case ComplianceCaseOpenedDomainEvent:
                eventType = ComplianceCaseCreated;
                payload = Envelope(messageId, eventType, domainEvent.OccurredAt, workflowId, correlationId, causationId, complianceCase,
                    new ComplianceCaseCreatedPayload(
                        complianceCase.Id,
                        complianceCase.ApplicationRef,
                        complianceCase.ApplicationNumber,
                        complianceCase.CustomerNumber,
                        complianceCase.BranchCode.Value,
                        complianceCase.KycCaseId,
                        complianceCase.Status.ToCode()));
                break;

            case ComplianceCaseDecidedDomainEvent decided:
                eventType = decided.NewStatus == ComplianceCaseStatus.Approved ? ComplianceCaseApproved : ComplianceCaseRejected;
                actedBy = decided.DecidedByUserId;
                payload = Envelope(messageId, eventType, decided.OccurredAt, workflowId, correlationId, causationId, complianceCase,
                    new ComplianceCaseDecisionPayload(
                        complianceCase.Id,
                        complianceCase.ApplicationRef,
                        complianceCase.ApplicationNumber,
                        complianceCase.CustomerNumber,
                        complianceCase.BranchCode.Value,
                        decided.PreviousStatus.ToCode(),
                        decided.NewStatus.ToCode(),
                        complianceCase.ScreeningOutcome?.ToCode(),
                        complianceCase.RiskRating?.ToCode(),
                        decided.DecidedByUserId,
                        decided.OccurredAt,
                        decided.Remarks));
                break;

            // Internal facts. Listed explicitly so that a NEW, unmapped event still fails loudly.
            case ScreeningCompletedDomainEvent:
            case ComplianceCaseAssignedDomainEvent:
            case ComplianceCaseReleasedDomainEvent:
            case ComplianceCaseHoldChangedDomainEvent:
                return null;

            default:
                throw new InvalidOperationException($"No integration event mapping exists for domain event {domainEvent.GetType().Name}.");
        }

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = complianceCase.Id.ToString(),
            EventType = eventType,
            Payload = payload,
            OccurredAt = domainEvent.OccurredAt,
            WorkflowId = workflowId,
            CorrelationId = correlationId,
            CausationId = causationId,
            InitiatedByUserId = complianceCase.InitiatedByUserId,
            ActedByUserId = actedBy,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }

    private static string Envelope<TPayload>(
        Guid messageId, string eventType, DateTimeOffset occurredAt, Guid? workflowId, Guid? correlationId, Guid causationId,
        ComplianceCase complianceCase, TPayload payload) =>
        JsonSerializer.Serialize(new ComplianceIntegrationEventEnvelope<TPayload>(
            messageId, eventType, SchemaVersion, Source, occurredAt, workflowId, correlationId, causationId,
            complianceCase.InitiatedByUserId, payload));
}