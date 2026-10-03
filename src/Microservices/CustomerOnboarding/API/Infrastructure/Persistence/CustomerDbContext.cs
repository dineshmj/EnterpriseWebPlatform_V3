using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public sealed class CustomerDbContext :
    DbContext,
    IApplicationUnitOfWork,
    ICustomerReadContext,
    IOnboardingApplicationReadContext,
    IInboxStore
{
    /// <summary>
    /// The only M2M client that may state a human initiator (X-Initiated-By-User-Id):
    /// the Customer Onboarding KYC subscriber, which copies it from the KYC event.
    /// </summary>
    private static readonly string TrustedInitiatorAssertingClient =
        EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.CustomerOnboardingMicroservice
            .CLIENT_ID_FOR_IDP_FOR_CUST_ONBOARDING_KYC_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M;

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CustomerDbContext(
        DbContextOptions<CustomerDbContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerAddress> CustomerAddresses =>
        Set<CustomerAddress>();

    public DbSet<OnboardingApplication> OnboardingApplications =>
        Set<OnboardingApplication>();

    public DbSet<OutboxMessage> OutboxMessages =>
        Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages =>
        Set<InboxMessage>();

    IQueryable<Customer> ICustomerReadContext.Customers =>
        Customers;

    IQueryable<OnboardingApplication>
        IOnboardingApplicationReadContext.OnboardingApplications =>
        OnboardingApplications;

    public override async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var domainEvents = CollectDomainEvents();

        if (domainEvents.Count == 0)
        {
            return await base.SaveChangesAsync(cancellationToken);
        }

        await using var transaction =
            await Database.BeginTransactionAsync(
                cancellationToken);

        try
        {
            // First persist the business state. PostgreSQL generates
            // database-generated IDs during this SaveChanges call.
            var result =
                await base.SaveChangesAsync(cancellationToken);

            // The generated Customer.Id is now available, so the integration
            // event can contain the real business aggregate identifier.
            var initiatedByUserId = GetInitiatedByUserId();

            var outboxMessages =
                await CreateOutboxMessagesAsync(
                    domainEvents,
                    initiatedByUserId,
                    cancellationToken);

            // Persist the Outbox rows using the SAME database transaction, one at a
            // time and in domain-event order. outbox_messages.sequence is assigned in
            // INSERT order, and EF Core does not guarantee that rows added in one
            // SaveChanges are inserted in the order they were added. The relay
            // publishes per aggregate in sequence order, so a cause (e.g. Submitted)
            // must be inserted before its effect (StatusChanged).
            foreach (var outboxMessage in outboxMessages)
            {
                OutboxMessages.Add(outboxMessage);
                await base.SaveChangesAsync(cancellationToken);
            }

            // Customer state + Outbox event become durable together.
            await transaction.CommitAsync(cancellationToken);

            ClearDomainEvents(domainEvents);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(CustomerDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    private List<(AggregateRoot Aggregate, IDomainEvent Event)>
        CollectDomainEvents()
    {
        return ChangeTracker
            .Entries<AggregateRoot>()
            .SelectMany(
                entry => entry.Entity.DomainEvents.Select(
                    domainEvent => (
                        Aggregate: entry.Entity,
                        Event: domainEvent)))
            .ToList();
    }

    private async Task<List<OutboxMessage>> CreateOutboxMessagesAsync(
        IEnumerable<(AggregateRoot Aggregate, IDomainEvent Event)> domainEvents,
        Guid? initiatedByUserId,
        CancellationToken cancellationToken)
    {
        var messages = new List<OutboxMessage>();
        var requestContext = GetWorkflowRequestContext();
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

                    var envelope = new IntegrationEventEnvelope<CustomerCreatedIntegrationEvent>(
                        messageId,
                        "CustomerCreated",
                        "customer-onboarding",
                        customerCreated.OccurredAt,
                        workflowId,
                        correlationId,
                        requestContext.CausationId,
                        initiatedByUserId?.ToString(),
                        integrationEvent);

                    var payload = JsonSerializer.SerializeToDocument(envelope);

                    messages.Add(OutboxMessage.Create(
                        messageId,
                        "Customer",
                        customer.Id.ToString(),
                        "CustomerCreated",
                        payload,
                        customerCreated.OccurredAt,
                        workflowId,
                        correlationId,
                        requestContext.CausationId,
                        initiatedByUserId));

                    break;
                }

                case OnboardingApplicationSubmittedDomainEvent submitted:
                {
                    var context = await ResolveApplicationWorkflowContextAsync(
                        submitted.CustomerId,
                        submitted.ApplicationId,
                        requestContext,
                        lastMessageIdByAggregate,
                        cancellationToken);

                    var messageId = Guid.NewGuid();
                    var causationId = ResolveCausationId(
                        submitted.ApplicationId,
                        context.CausationId,
                        lastMessageIdByAggregate);

                    var customerNumber = await Customers
                        .AsNoTracking()
                        .Where(x => x.Id == submitted.CustomerId)
                        .Select(x => x.CustomerNumber.Value)
                        .SingleAsync(cancellationToken);

                    var integrationEvent = new OnboardingApplicationSubmittedIntegrationEvent(
                        submitted.ApplicationId,
                        submitted.CustomerId,
                        submitted.ApplicationNumber,
                        customerNumber);

                    var envelope = new IntegrationEventEnvelope<OnboardingApplicationSubmittedIntegrationEvent>(
                        messageId,
                        "OnboardingApplicationSubmitted",
                        "customer-onboarding",
                        submitted.OccurredAt,
                        context.WorkflowId,
                        context.CorrelationId,
                        causationId,
                        initiatedByUserId?.ToString(),
                        integrationEvent);

                    messages.Add(OutboxMessage.Create(
                        messageId,
                        "OnboardingApplication",
                        submitted.ApplicationId.ToString(),
                        "OnboardingApplicationSubmitted",
                        JsonSerializer.SerializeToDocument(envelope),
                        submitted.OccurredAt,
                        context.WorkflowId,
                        context.CorrelationId,
                        causationId,
                        initiatedByUserId));

                    lastMessageIdByAggregate[GetAggregateKey("OnboardingApplication", submitted.ApplicationId)] = messageId;
                    break;
                }

                case OnboardingApplicationStatusChangedDomainEvent statusChanged:
                {
                    var context = await ResolveApplicationWorkflowContextAsync(
                        statusChanged.CustomerId,
                        statusChanged.ApplicationId,
                        requestContext,
                        lastMessageIdByAggregate,
                        cancellationToken);

                    var messageId = Guid.NewGuid();
                    var causationId = ResolveCausationId(
                        statusChanged.ApplicationId,
                        context.CausationId,
                        lastMessageIdByAggregate);

                    var integrationEvent = new OnboardingApplicationStatusChangedIntegrationEvent(
                        statusChanged.ApplicationId,
                        statusChanged.CustomerId,
                        statusChanged.PreviousStatus.ToCode(),
                        statusChanged.NewStatus.ToCode());

                    var envelope = new IntegrationEventEnvelope<OnboardingApplicationStatusChangedIntegrationEvent>(
                        messageId,
                        "OnboardingApplicationStatusChanged",
                        "customer-onboarding",
                        statusChanged.OccurredAt,
                        context.WorkflowId,
                        context.CorrelationId,
                        causationId,
                        initiatedByUserId?.ToString(),
                        integrationEvent);

                    messages.Add(OutboxMessage.Create(
                        messageId,
                        "OnboardingApplication",
                        statusChanged.ApplicationId.ToString(),
                        "OnboardingApplicationStatusChanged",
                        JsonSerializer.SerializeToDocument(envelope),
                        statusChanged.OccurredAt,
                        context.WorkflowId,
                        context.CorrelationId,
                        causationId,
                        initiatedByUserId));

                    lastMessageIdByAggregate[GetAggregateKey("OnboardingApplication", statusChanged.ApplicationId)] = messageId;
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"No Outbox mapping exists for domain event '{domainEvent.GetType().Name}'.");
            }
        }

        return messages;
    }

    private async Task<WorkflowRequestContext> ResolveApplicationWorkflowContextAsync(
        long customerId,
        long applicationId,
        WorkflowRequestContext requestContext,
        IReadOnlyDictionary<string, Guid> localMessages,
        CancellationToken cancellationToken)
    {
        var applicationAggregateKey = GetAggregateKey("OnboardingApplication", applicationId);
        var existingApplicationMessage = await OutboxMessages
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

        var customerRootMessage = await OutboxMessages
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
            (localMessages.TryGetValue(applicationAggregateKey, out var localMessageId)
                ? localMessageId
                : customerRootMessage?.MessageId));
    }

    private Guid? ResolveCausationId(
        long applicationId,
        Guid? requestedCausationId,
        IReadOnlyDictionary<string, Guid> localMessages)
    {
        var aggregateKey = GetAggregateKey("OnboardingApplication", applicationId);
        if (localMessages.TryGetValue(aggregateKey, out var localMessageId))
        {
            // When multiple domain events are raised in one transaction, the later
            // integration event is caused by the earlier integration event.
            return localMessageId;
        }

        return requestedCausationId;
    }

    private WorkflowRequestContext GetWorkflowRequestContext()
    {
        var headers = _httpContextAccessor.HttpContext?.Request.Headers;
        return new WorkflowRequestContext(
            ParseGuidHeader(headers, "X-Workflow-Id"),
            ParseGuidHeader(headers, "X-Correlation-Id"),
            ParseGuidHeader(headers, "X-Causation-Id"));
    }

    private static Guid? ParseGuidHeader(
        IHeaderDictionary? headers,
        string name)
    {
        return headers is not null &&
               headers.TryGetValue(name, out var value) &&
               Guid.TryParse(value.FirstOrDefault(), out var parsed)
            ? parsed
            : null;
    }

    private static string GetAggregateKey(string aggregateType, long aggregateId) =>
        $"{aggregateType}:{aggregateId}";

    private sealed record WorkflowRequestContext(
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid? CausationId);

    private sealed record WorkflowLookup(
        Guid? WorkflowId,
        Guid? CorrelationId,
        Guid MessageId);

    private Guid? GetInitiatedByUserId()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var subject = httpContext?.User.FindFirst("sub")?.Value;

        if (subject is not null)
        {
            // A human caller is the initiator.
            return Guid.TryParse(subject, out var subjectId) ? subjectId : null;
        }

        // An M2M caller has no human subject. Only the pinned KYC subscriber may
        // carry the ORIGINAL human initiator forward (it copies it from the KYC
        // event), so the workflow's accountability survives the asynchronous hop.
        // It is attribution only - never an authorization grant.
        var clientId = httpContext?.User.FindFirst("client_id")?.Value;
        if (string.Equals(clientId, TrustedInitiatorAssertingClient, StringComparison.Ordinal) &&
            Guid.TryParse(httpContext!.Request.Headers["X-Initiated-By-User-Id"].FirstOrDefault(), out var initiator))
        {
            return initiator;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // IInboxStore
    // ------------------------------------------------------------------

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages
            .AsNoTracking()
            .AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    public void RecordProcessed(Guid messageId, string consumer)
    {
        var now = DateTimeOffset.UtcNow;
        var inboxMessage = InboxMessage.Create(messageId, consumer, now);
        inboxMessage.MarkProcessed(now);
        InboxMessages.Add(inboxMessage);
    }

    private static void ClearDomainEvents(
        IEnumerable<(AggregateRoot Aggregate, IDomainEvent Event)>
            domainEvents)
    {
        domainEvents
            .Select(x => x.Aggregate)
            .Distinct()
            .ToList()
            .ForEach(x => x.ClearDomainEvents());
    }
}