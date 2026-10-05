using System.Text.Json;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;

// Published contracts (topics accounts.*), in the platform's standard envelope.

public sealed record AccountsIntegrationEventEnvelope<TPayload>(
    Guid MessageId,
    string EventType,
    int SchemaVersion,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? InitiatedByUserId,
    TPayload Payload,
    string? InitiatedByLanId = null);

public sealed record AccountApplicationCreatedPayload(
    long AccountApplicationId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    long ComplianceCaseId,
    string Status,
    string HolderName);

public sealed record AccountApplicationRejectedPayload(
    long AccountApplicationId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string PreviousStatus,
    string NewStatus,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string DecisionRemarks,
    string HolderName,
    string? DecisionByLanId);

public sealed record AccountOpenedPayload(
    long AccountApplicationId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string AccountNumber,
    string Bsb,
    string Product,
    string ApprovedByUserId,
    DateTimeOffset OpenedAt,
    string HolderName,
    string? ApprovedByLanId);

public sealed record AccountOpeningFailedPayload(
    long AccountApplicationId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string Reason,
    int Attempts,
    DateTimeOffset FailedAt,
    string HolderName);

/// <summary>
/// Translates Accounts domain events into published integration events (Outbox rows):
/// the single place where the domain's language meets the public contract. Assignment,
/// hold changes and the approval itself are internal and are not published.
/// </summary>
internal static class AccountsIntegrationEventMapper
{
    public const string AggregateType = "AccountApplication";
    public const string AccountApplicationCreated = "AccountApplicationCreated";
    public const string AccountApplicationRejected = "AccountApplicationRejected";
    public const string AccountOpened = "AccountOpened";
    public const string AccountOpeningFailed = "AccountOpeningFailed";

    private const string Source = "accounts";
    private const int SchemaVersion = 1;

    public static OutboxMessage? ToOutboxMessage(
        AccountApplication application,
        IDomainEvent domainEvent,
        Guid? workflowId,
        Guid? correlationId,
        Guid causationId,
        Func<string?, string?> lanOf)
    {
        var messageId = Guid.NewGuid();
        string eventType;
        string payload;
        string? actedBy = null;

        switch (domainEvent)
        {
            case AccountApplicationCreatedDomainEvent created:
                eventType = AccountApplicationCreated;
                payload = Envelope(messageId, eventType, created.OccurredAt, workflowId, correlationId, causationId, application, lanOf,
                    new AccountApplicationCreatedPayload(
                        application.Id,
                        application.ApplicationRef,
                        application.ApplicationNumber,
                        application.CustomerNumber,
                        application.BranchCode.Value,
                        application.ComplianceCaseId,
                        application.Status.ToCode(),
                        application.HolderName.FullName));
                break;

            case AccountApplicationRejectedDomainEvent rejected:
                eventType = AccountApplicationRejected;
                actedBy = rejected.DecidedByUserId;
                payload = Envelope(messageId, eventType, rejected.OccurredAt, workflowId, correlationId, causationId, application, lanOf,
                    new AccountApplicationRejectedPayload(
                        application.Id,
                        application.ApplicationRef,
                        application.ApplicationNumber,
                        application.CustomerNumber,
                        application.BranchCode.Value,
                        rejected.PreviousStatus.ToCode(),
                        AccountApplicationStatus.Rejected.ToCode(),
                        rejected.DecidedByUserId,
                        rejected.OccurredAt,
                        rejected.Remarks,
                        application.HolderName.FullName,
                        lanOf(rejected.DecidedByUserId)));
                break;

            case AccountOpenedDomainEvent opened:
                eventType = AccountOpened;
                actedBy = application.DecisionByUserId;
                payload = Envelope(messageId, eventType, opened.OccurredAt, workflowId, correlationId, causationId, application, lanOf,
                    new AccountOpenedPayload(
                        application.Id,
                        application.ApplicationRef,
                        application.ApplicationNumber,
                        application.CustomerNumber,
                        application.BranchCode.Value,
                        opened.AccountNumber,
                        opened.Bsb,
                        opened.Product.ToCode(),
                        application.DecisionByUserId ?? string.Empty,
                        opened.OccurredAt,
                        application.HolderName.FullName,
                        lanOf(application.DecisionByUserId)));
                break;

            case AccountOpeningFailedDomainEvent failed:
                eventType = AccountOpeningFailed;
                payload = Envelope(messageId, eventType, failed.OccurredAt, workflowId, correlationId, causationId, application, lanOf,
                    new AccountOpeningFailedPayload(
                        application.Id,
                        application.ApplicationRef,
                        application.ApplicationNumber,
                        application.CustomerNumber,
                        application.BranchCode.Value,
                        failed.Reason,
                        failed.Attempts,
                        failed.OccurredAt,
                        application.HolderName.FullName));
                break;

            // Internal facts. Listed explicitly so that a NEW, unmapped event still fails loudly.
            case AccountApplicationAssignedDomainEvent:
            case AccountApplicationReleasedDomainEvent:
            case AccountApplicationHoldChangedDomainEvent:
            case AccountApplicationApprovedDomainEvent:
                return null;

            default:
                throw new InvalidOperationException($"No integration event mapping exists for domain event {domainEvent.GetType().Name}.");
        }

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = application.Id.ToString(),
            EventType = eventType,
            Payload = payload,
            OccurredAt = domainEvent.OccurredAt,
            WorkflowId = workflowId,
            CorrelationId = correlationId,
            CausationId = causationId,
            InitiatedByUserId = application.InitiatedByUserId,
            ActedByUserId = actedBy,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }

    private static string Envelope<TPayload>(
        Guid messageId, string eventType, DateTimeOffset occurredAt, Guid? workflowId, Guid? correlationId, Guid causationId,
        AccountApplication application, Func<string?, string?> lanOf, TPayload payload) =>
        JsonSerializer.Serialize(new AccountsIntegrationEventEnvelope<TPayload>(
            messageId, eventType, SchemaVersion, Source, occurredAt, workflowId, correlationId, causationId,
            application.InitiatedByUserId, payload, lanOf(application.InitiatedByUserId)));
}