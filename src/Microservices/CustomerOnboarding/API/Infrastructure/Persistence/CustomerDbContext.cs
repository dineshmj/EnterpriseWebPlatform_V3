using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public sealed class CustomerDbContext :
    DbContext,
    IApplicationUnitOfWork,
    ICustomerReadContext,
    IOnboardingApplicationReadContext
{
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
                CreateOutboxMessages(domainEvents, initiatedByUserId);

            OutboxMessages.AddRange(outboxMessages);

            // Persist the Outbox rows using the SAME database transaction.
            await base.SaveChangesAsync(cancellationToken);

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

    private List<OutboxMessage> CreateOutboxMessages(
        IEnumerable<(AggregateRoot Aggregate, IDomainEvent Event)>
            domainEvents,
        Guid? initiatedByUserId)
    {
        var messages = new List<OutboxMessage>();

        foreach (var (aggregate, domainEvent) in domainEvents)
        {
            switch (domainEvent)
            {
                case CustomerCreatedDomainEvent customerCreated:
                    {
                        var customer =
                            (Customer)aggregate;

                        var integrationEvent =
                            new CustomerCreatedIntegrationEvent(
                                customer.Id,
                                customer.CustomerNumber.Value,
                                customer.SubjectId,
                                customer.CustomerType
                                    .ToString()
                                    .ToUpperInvariant(),
                                customer.Status
                                    .ToString()
                                    .ToUpperInvariant());

                        // CustomerCreated is the root event for this workflow.
                        // A new WorkflowId and CorrelationId are therefore
                        // established here. CausationId is null because there
                        // is no preceding integration event.
                        var workflowId = Guid.NewGuid();
                        var correlationId = Guid.NewGuid();
                        var messageId = Guid.NewGuid();

                        var envelope =
                            new IntegrationEventEnvelope
                                <CustomerCreatedIntegrationEvent>(
                                messageId,
                                "CustomerCreated",
                                "customer-onboarding",
                                customerCreated.OccurredAt,
                                workflowId,
                                correlationId,
                                null,
                                initiatedByUserId?.ToString(),
                                integrationEvent);

                        var payload =
                            JsonSerializer.SerializeToDocument(
                                envelope);

                        messages.Add(
                            OutboxMessage.Create(
                                "Customer",
                                customer.Id.ToString(),
                                "CustomerCreated",
                                payload,
                                customerCreated.OccurredAt,
                                workflowId,
                                correlationId,
                                null,
                                initiatedByUserId));

                        break;
                    }

                case OnboardingApplicationSubmittedDomainEvent submitted:
                    {
                        var integrationEvent =
                            new OnboardingApplicationSubmittedIntegrationEvent(
                                submitted.ApplicationId,
                                submitted.CustomerId,
                                submitted.ApplicationNumber);

                        var envelope =
                            new IntegrationEventEnvelope
                                <OnboardingApplicationSubmittedIntegrationEvent>(
                                Guid.NewGuid(),
                                "OnboardingApplicationSubmitted",
                                "customer-onboarding",
                                submitted.OccurredAt,
                                null,
                                null,
                                null,
                                initiatedByUserId?.ToString(),
                                integrationEvent);

                        var payload =
                            JsonSerializer.SerializeToDocument(
                                envelope);

                        messages.Add(
                            OutboxMessage.Create(
                                "OnboardingApplication",
                                submitted.ApplicationId.ToString(),
                                "OnboardingApplicationSubmitted",
                                payload,
                                submitted.OccurredAt,
                                null,
                                null,
                                null,
                                initiatedByUserId));

                        break;
                    }

                case OnboardingApplicationStatusChangedDomainEvent statusChanged:
                    {
                        var integrationEvent =
                            new OnboardingApplicationStatusChangedIntegrationEvent(
                                statusChanged.ApplicationId,
                                statusChanged.CustomerId,
                                statusChanged.PreviousStatus
                                    .ToString()
                                    .ToUpperInvariant(),
                                statusChanged.NewStatus
                                    .ToString()
                                    .ToUpperInvariant());

                        var envelope =
                            new IntegrationEventEnvelope
                                <OnboardingApplicationStatusChangedIntegrationEvent>(
                                Guid.NewGuid(),
                                "OnboardingApplicationStatusChanged",
                                "customer-onboarding",
                                statusChanged.OccurredAt,
                                null,
                                null,
                                null,
                                initiatedByUserId?.ToString(),
                                integrationEvent);

                        var payload =
                            JsonSerializer.SerializeToDocument(
                                envelope);

                        messages.Add(
                            OutboxMessage.Create(
                                "OnboardingApplication",
                                statusChanged.ApplicationId.ToString(),
                                "OnboardingApplicationStatusChanged",
                                payload,
                                statusChanged.OccurredAt,
                                null,
                                null,
                                null,
                                initiatedByUserId));

                        break;
                    }

                default:
                    throw new InvalidOperationException(
                        $"No Outbox mapping exists for domain event " +
                        $"'{domainEvent.GetType().Name}'.");
            }
        }

        return messages;
    }

    private Guid? GetInitiatedByUserId()
    {
        var subject = _httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value;

        return Guid.TryParse(subject, out var subjectId)
            ? subjectId
            : null;
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