using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Translates Customer Onboarding domain events into published integration events
/// (standard envelope, Outbox rows). The single place where internal domain language
/// meets the external contract: event names, status codes and workflow metadata.
/// </summary>
internal sealed class CustomerIntegrationEventMapper(CustomerDbContext db, PersonalDataCipher personalDataCipher)
{
    private const string Source = "customer-onboarding";

    private const string ErasureAggregateType = "PersonalDataErasureRequest";

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

        // The initiator's LAN ID (from this context's staff directory), named in every envelope.
        var initiator = initiatedByUserId?.ToString();
        _initiatedByLanId = initiator is null
            ? null
            : await db.StaffMembers.AsNoTracking()
                .Where(x => x.UserId == initiator)
                .Select(x => x.LanId)
                .FirstOrDefaultAsync(cancellationToken);

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

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ The applicant travels in clear (KYC verifies the evidence against it), so this
                    // │ copy is bounded instead: the topic keeps it 7 days, and the relay blanks this
                    // │ Outbox payload after the same period. The stored fields stay encrypted.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    var person = (await new CustomerPersonalData(db, personalDataCipher)
                        .ReadAsync([submitted.CustomerId], cancellationToken))[submitted.CustomerId];

                    // The primary residential address: the one KYC verifies and Compliance screens.
                    var residential = person.ResidentialAddress;
                    var address = residential is null
                        ? null
                        : new AddressReference(
                            residential.AddressLine1,
                            residential.AddressLine2,
                            residential.City,
                            residential.State,
                            residential.PostalCode,
                            residential.CountryCode);

                    var integrationEvent = new OnboardingApplicationSubmittedIntegrationEvent(
                        submitted.ApplicationId,
                        submitted.ApplicationRef,
                        submitted.CustomerId,
                        submitted.ApplicationNumber,
                        customerNumber,
                        submitted.BranchCode,
                        [.. submitted.EvidenceDocuments.Select(x => new EvidenceDocumentReference(x.DocumentId, x.DocumentType))],
                        new ApplicantReference(person.FirstName, person.LastName, address));

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

                case OnboardingApplicationRejectedDomainEvent rejected:
                {
                    var context = await ResolveApplicationWorkflowContextAsync(
                        rejected.CustomerId, rejected.ApplicationId, requestContext,
                        lastMessageIdByAggregate, cancellationToken);

                    var messageId = Guid.NewGuid();
                    var causationId = ResolveCausationId(
                        rejected.ApplicationId, context.CausationId, lastMessageIdByAggregate);

                    var customerNumber = await db.Customers
                        .AsNoTracking()
                        .Where(x => x.Id == rejected.CustomerId)
                        .Select(x => x.CustomerNumber.Value)
                        .SingleAsync(cancellationToken);

                    var integrationEvent = new OnboardingApplicationRejectedIntegrationEvent(
                        rejected.ApplicationRef,
                        rejected.ApplicationNumber,
                        customerNumber,
                        rejected.BranchCode,
                        rejected.Stage.ToCode(),
                        rejected.PreviousStatus.ToCode(),
                        [.. rejected.EvidenceDocuments.Select(x => new EvidenceDocumentReference(x.DocumentId, x.DocumentType))]);

                    messages.Add(Envelope(
                        messageId, "OnboardingApplication", rejected.ApplicationId,
                        "OnboardingApplicationRejected", integrationEvent, rejected.OccurredAt,
                        context.WorkflowId, context.CorrelationId, causationId, initiatedByUserId));

                    lastMessageIdByAggregate[ApplicationKey(rejected.ApplicationId)] = messageId;
                    break;
                }

                case PersonalDataErasureRequestedDomainEvent requested:
                {
                    var request = (PersonalDataErasureRequest)aggregate;
                    messages.Add(await ErasureEnvelopeAsync(
                        "PersonalDataErasureRequested", request.Id, requested.CustomerId, requested.CustomerNumber,
                        requested.Reason, "PENDING", requested.RequestedByUserId, null, requested.OccurredAt,
                        requestContext, cancellationToken));
                    break;
                }

                case PersonalDataErasureRejectedDomainEvent rejectedErasure:
                    messages.Add(await ErasureEnvelopeAsync(
                        "PersonalDataErasureRejected", rejectedErasure.ErasureRequestId, rejectedErasure.CustomerId,
                        rejectedErasure.CustomerNumber, rejectedErasure.Reason, "REJECTED", rejectedErasure.RequestedByUserId,
                        rejectedErasure.DecidedByUserId, rejectedErasure.OccurredAt, requestContext, cancellationToken));
                    break;

                case CustomerPersonalDataErasedDomainEvent erased:
                    messages.Add(await ErasureEnvelopeAsync(
                        "CustomerPersonalDataErased", erased.ErasureRequestId, erased.CustomerId, erased.CustomerNumber,
                        erased.Reason, "ERASED", erased.RequestedByUserId, erased.DecidedByUserId, erased.OccurredAt,
                        requestContext, cancellationToken));
                    break;

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

    private string? _initiatedByLanId;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ An erasure's events name BOTH people for the audit trail: the envelope's initiator is
    // │ always the requester (also on the decision, made by someone else), DecisionBy* the
    // │ second person - each with the LAN ID this context's staff directory holds for them.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private async Task<OutboxMessage> ErasureEnvelopeAsync(
        string eventType,
        long erasureRequestId,
        long customerId,
        string customerNumber,
        Domain.Enums.PersonalDataErasureReason reason,
        string status,
        string requestedByUserId,
        string? decidedByUserId,
        DateTimeOffset occurredAt,
        WorkflowRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var people = new[] { requestedByUserId, decidedByUserId }.OfType<string>().ToList();
        var lanIds = await db.StaffMembers.AsNoTracking()
            .Where(x => people.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => x.LanId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var integrationEvent = new PersonalDataErasureIntegrationEvent(
            erasureRequestId,
            customerId,
            customerNumber,
            reason.ToCode(),
            status,
            decidedByUserId,
            decidedByUserId is not null && lanIds.TryGetValue(decidedByUserId, out var deciderLanId) ? deciderLanId : null);

        // One workflow per erasure request: the decision continues the request's workflow and is caused by it.
        var requestMessage = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.AggregateType == ErasureAggregateType && x.AggregateId == erasureRequestId.ToString())
            .OrderBy(x => x.Sequence)
            .Select(x => new WorkflowLookup(x.WorkflowId, x.CorrelationId, x.Id))
            .FirstOrDefaultAsync(cancellationToken);

        return Envelope(
            Guid.NewGuid(), ErasureAggregateType, erasureRequestId, eventType, integrationEvent, occurredAt,
            requestContext.WorkflowId ?? requestMessage?.WorkflowId ?? Guid.NewGuid(),
            requestContext.CorrelationId ?? requestMessage?.CorrelationId ?? Guid.NewGuid(),
            requestContext.CausationId ?? requestMessage?.MessageId,
            Guid.TryParse(requestedByUserId, out var requester) ? requester : null,
            lanIds.TryGetValue(requestedByUserId, out var requesterLanId) ? requesterLanId : null);
    }

    private OutboxMessage Envelope<TEvent>(
        Guid messageId,
        string aggregateType,
        long aggregateId,
        string eventType,
        TEvent integrationEvent,
        DateTimeOffset occurredAt,
        Guid? workflowId,
        Guid? correlationId,
        Guid? causationId,
        Guid? initiatedByUserId,
        string? initiatedByLanId = null)
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
            integrationEvent,
            initiatedByLanId ?? _initiatedByLanId);

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
        Dictionary<string, Guid> localMessages,
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
        Dictionary<string, Guid> localMessages) =>
        // When several domain events are raised in one transaction, each later
        // integration event is caused by the earlier one.
        localMessages.TryGetValue(ApplicationKey(applicationId), out var localMessageId)
            ? localMessageId
            : requestedCausationId;

    private static string ApplicationKey(long applicationId) => $"OnboardingApplication:{applicationId}";

    private sealed record WorkflowLookup(Guid? WorkflowId, Guid? CorrelationId, Guid MessageId);
}