using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
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
    private readonly WorkflowContextAccessor _workflowContext;

    public CustomerDbContext(
        DbContextOptions<CustomerDbContext> options,
        WorkflowContextAccessor workflowContext)
        : base(options)
    {
        _workflowContext = workflowContext;
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

    public DbSet<StaffMember> StaffMembers =>
        Set<StaffMember>();

    IQueryable<Customer> ICustomerReadContext.Customers =>
        Customers;

    IQueryable<OnboardingApplication>
        IOnboardingApplicationReadContext.OnboardingApplications =>
        OnboardingApplications;

    /// <summary>
    /// Unit of work: the aggregates' new state, the Outbox rows translated from their
    /// domain events, and (for consumers) the Inbox record commit in ONE transaction.
    /// </summary>
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

            // The generated IDs are now available to the integration events.
            var outboxMessages =
                await new CustomerIntegrationEventMapper(this).ToOutboxMessagesAsync(
                    domainEvents,
                    _workflowContext.GetRequestContext(),
                    _workflowContext.GetInitiatedByUserId(),
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

            // Business state + Outbox events become durable together.
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