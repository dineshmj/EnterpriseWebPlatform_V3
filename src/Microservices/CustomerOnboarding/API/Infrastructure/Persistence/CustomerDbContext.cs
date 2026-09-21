using System.Text.Json;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public sealed class CustomerDbContext :
    DbContext,
    IApplicationUnitOfWork,
    ICustomerReadContext,
    IOnboardingApplicationReadContext
{
    public CustomerDbContext(
        DbContextOptions<CustomerDbContext> options)
        : base(options)
    {
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
            var outboxMessages =
                CreateOutboxMessages(domainEvents);

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
            domainEvents)
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

                    var envelope =
                        new IntegrationEventEnvelope
                            <CustomerCreatedIntegrationEvent>(
                            Guid.NewGuid(),
                            "CustomerCreated",
                            "customer-onboarding",
                            customerCreated.OccurredAt,
                            null,
                            null,
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
                            customerCreated.OccurredAt));

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
