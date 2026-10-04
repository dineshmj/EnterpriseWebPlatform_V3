using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Translates Customer Onboarding domain events into published integration events
/// (standard envelope, Outbox rows). The single place where internal domain language
/// meets the external contract: event names, status codes and workflow metadata.
/// </summary>
internal sealed class CustomerIntegrationEventMapper(CustomerDbContext db)
{
    private const string Source = "customer-onboarding";

    /// <summary>Contract version of the published payloads (additive changes keep it).</summary>
    private const int SchemaVersion = 1;

    public async Task<List<OutboxMessage>> ToOutboxMessagesAsync(
        IEnumerable<(AggregateRoot Aggregate, IDomainEvent Event)> domainEvents,
        WorkflowRequestContext requestContext,
        Guid? initiatedByUserId,
        CancellationToken cancellationToken)
    {
        var messages = new List<OutboxMessage>();
        var lastMessageIdByAggregate = new Dictionary<string, Guid>();

        foreach (var (aggregate, domainEvent) in domainEvents)
        {
            switch (domainEvent)
            {
                case CustomerCreatedDomainEvent customerCreated:
                {
                    var customer = (Customer)aggregate;
                    var workflowId = requestContext.WorkflowId ?? Guid.NewGuid();
                    var correlationId = requestContext.CorrelationId ?? Guid.NewGuid();
                    var messageId = Guid.NewGuid();

                    var integrationEvent = new CustomerCreatedIntegrationEvent(
                        customer.Id,
                        customer.CustomerNumber.Value,
                        customer.SubjectId,
                        customer.CustomerType.ToString().ToUpperInvariant(),
                        customer.Status.ToString().ToUpperInvariant());

                    messages.Add(Envelope(
                        messageId, "Customer", customer.Id, "CustomerCreated", integrationEvent,
                        customerCreated.OccurredAt, workflowId, correlationId,
                        requestContext.CausationId, initiatedByUserId));
                    break;
                }

                case OnboardingApplicationSubmittedDomainEvent submitted:
                {
                    var context = await ResolveApplicationWorkflowContextAsync(
                        submitted.CustomerId, submitted.ApplicationId, requestContext,
                        lastMessageIdByAggregate, cancellationToken);

                    var messageId = Guid.NewGuid();
                    var causationId = ResolveCausationId(
                        submitted.ApplicationId, context.CausationId, lastMessageIdByAggregate);

                    var customerNumber = await db.Customers
                        .AsNoTracking()
                        .Where(x => x.Id == submitted.CustomerId)
                        .Select(x => x.CustomerNumber.Value)
                        .SingleAsync(cancellationToken);

                    var integrationEvent = new OnboardingApplicationSubmittedIntegrationEvent(
                        submitted.ApplicationId,
                        submitted.ApplicationRef,
                        submitted.CustomerId,
                        submitted.ApplicationNumber,
                        customerNumber,
                        submitted.BranchCode);

                    messages.Add(Envelope(
                        messageId, "OnboardingApplication", submitted.ApplicationId,
                        "OnboardingApplicationSubmitted", integrationEvent, submitted.OccurredAt,
                        context.WorkflowId, context.CorrelationId, causationId, initiatedByUserId));

                    lastMessageIdByAggregate[ApplicationKey(submitted.ApplicationId)] = messageId;
                    break;
                }

                case OnboardingApplicationStatusChangedDomainEvent statusChanged:
                {
                    var context = await ResolveApplicationWorkflowContextAsync(
                        statusChanged.CustomerId, statusChanged.ApplicationId, requestContext,
                        lastMessageIdByAggregate, cancellationToken);

                    var messageId = Guid.NewGuid();
                    var causationId = ResolveCausationId(
                        statusChanged.ApplicationId, context.CausationId, lastMessageIdByAggregate);

                    var integrationEvent = new OnboardingApplicationStatusChangedIntegrationEvent(
                        statusChanged.ApplicationId,
                        statusChanged.ApplicationRef,
                        statusChanged.CustomerId,
                        statusChanged.PreviousStatus.ToCode(),
                        statusChanged.NewStatus.ToCode());

                    messages.Add(Envelope(
                        messageId, "OnboardingApplication", statusChanged.ApplicationId,
                        "OnboardingApplicationStatusChanged", integrationEvent, statusChanged.OccurredAt,
                        context.WorkflowId, context.CorrelationId, causationId, initiatedByUserId));

                    lastMessageIdByAggregate[ApplicationKey(statusChanged.ApplicationId)] = messageId;
                    break;
                }

                // Internal facts with no published contract yet (no topic, no consumer).
                // Listed explicitly so that a NEW, unmapped domain event still fails loudly.
                case CustomerStatusChangedDomainEvent:
                case CustomerContactDetailsChangedDomainEvent:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"No Outbox mapping exists for domain event '{domainEvent.GetType().Name}'.");
            }
        }

        return messages;
    }

    private static OutboxMessage Envelope<TEvent>(
        Guid messageId,
        string aggregateType,
        long aggregateId,
        string eventType,
        TEvent integrationEvent,
        DateTimeOffset occurredAt,
        Guid? workflowId,
        Guid? correlationId,
        Guid? causationId,
        Guid? initiatedByUserId)
    {
        var envelope = new IntegrationEventEnvelope<TEvent>(
            messageId,
            eventType,
            SchemaVersion,
            Source,
            occurredAt,
            workflowId,
            correlationId,
            causationId,
            initiatedByUserId?.ToString(),
            integrationEvent);

        return OutboxMessage.Create(
            messageId,
            aggregateType,
            aggregateId.ToString(),
            eventType,
            JsonSerializer.SerializeToDocument(envelope),
            occurredAt,
            workflowId,
            correlationId,
            causationId,
            initiatedByUserId,
            MessagingTelemetry.CurrentTraceParent());
    }

    private async Task<WorkflowRequestContext> ResolveApplicationWorkflowContextAsync(
        long customerId,
        long applicationId,
        WorkflowRequestContext requestContext,
        IReadOnlyDictionary<string, Guid> localMessages,
        CancellationToken cancellationToken)
    {
        var existingApplicationMessage = await db.OutboxMessages
            .AsNoTracking()
            .Where(x => x.AggregateType == "OnboardingApplication" &&
                        x.AggregateId == applicationId.ToString())
            .OrderByDescending(x => x.Sequence)
            .Select(x => new WorkflowLookup(x.WorkflowId, x.CorrelationId, x.Id))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingApplicationMessage is not null)
        {
            return new WorkflowRequestContext(
                requestContext.WorkflowId ?? existingApplicationMessage.WorkflowId,
                requestContext.CorrelationId ?? existingApplicationMessage.CorrelationId,
                requestContext.CausationId ?? existingApplicationMessage.MessageId);
        }

        var customerRootMessage = await db.OutboxMessages
            .AsNoTracking()
            .Where(x => x.AggregateType == "Customer" &&
                        x.AggregateId == customerId.ToString() &&
                        x.EventType == "CustomerCreated")
            .OrderBy(x => x.Sequence)
            .Select(x => new WorkflowLookup(x.WorkflowId, x.CorrelationId, x.Id))
            .FirstOrDefaultAsync(cancellationToken);

        return new WorkflowRequestContext(
            requestContext.WorkflowId ?? customerRootMessage?.WorkflowId ?? Guid.NewGuid(),
            requestContext.CorrelationId ?? customerRootMessage?.CorrelationId ?? Guid.NewGuid(),
            requestContext.CausationId ??
            (localMessages.TryGetValue(ApplicationKey(applicationId), out var localMessageId)
                ? localMessageId
                : customerRootMessage?.MessageId));
    }

    private static Guid? ResolveCausationId(
        long applicationId,
        Guid? requestedCausationId,
        IReadOnlyDictionary<string, Guid> localMessages) =>
        // When several domain events are raised in one transaction, each later
        // integration event is caused by the earlier one.
        localMessages.TryGetValue(ApplicationKey(applicationId), out var localMessageId)
            ? localMessageId
            : requestedCausationId;

    private static string ApplicationKey(long applicationId) => $"OnboardingApplication:{applicationId}";

    private sealed record WorkflowLookup(Guid? WorkflowId, Guid? CorrelationId, Guid MessageId);
}