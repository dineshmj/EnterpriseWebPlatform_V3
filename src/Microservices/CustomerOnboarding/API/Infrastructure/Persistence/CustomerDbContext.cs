using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

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

    public DbSet<CustomerAddress> CustomerAddresses
        => Set<CustomerAddress>();

    public DbSet<OnboardingApplication> OnboardingApplications
        => Set<OnboardingApplication>();

    public DbSet<OutboxMessage> OutboxMessages
        => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages
        => Set<InboxMessage>();

    IQueryable<Customer> ICustomerReadContext.Customers
        => Customers;

    IQueryable<OnboardingApplication>
        IOnboardingApplicationReadContext.OnboardingApplications
        => OnboardingApplications;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(CustomerDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}