using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly CustomerDbContext _dbContext;
    private readonly CustomerPersonalData _personalData;

    public CustomerRepository(CustomerDbContext dbContext, CustomerPersonalData personalData)
    {
        _dbContext = dbContext;
        _personalData = personalData;
    }

    public async Task<Customer?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var customer = await _dbContext.Customers
            .Include(x => x.Addresses)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return await RevealedAsync(customer, cancellationToken);
    }

    public async Task<Customer?> GetBySubjectIdAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        var customer = await _dbContext.Customers
            .Include(x => x.Addresses)
            .SingleOrDefaultAsync(
                x => x.SubjectId == subjectId,
                cancellationToken);
        return await RevealedAsync(customer, cancellationToken);
    }

    public async Task<Customer?> GetByCustomerNumberAsync(
        string customerNumber,
        CancellationToken cancellationToken = default)
    {
        var customer = await _dbContext.Customers
            .SingleOrDefaultAsync(
                x => (string)(object)x.CustomerNumber == customerNumber,
                cancellationToken);
        return await RevealedAsync(customer, cancellationToken);
    }

    public async Task AddAsync(
        Customer customer,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Customers.AddAsync(customer, cancellationToken);
    }

    public void Remove(Customer customer)
    {
        _dbContext.Customers.Remove(customer);
    }

    // SEC: an aggregate leaves the repository only with its personal fields decrypted (CustomerPersonalData).
    private async Task<Customer?> RevealedAsync(Customer? customer, CancellationToken cancellationToken)
    {
        if (customer is not null)
            await _personalData.RevealAsync(customer, cancellationToken);
        return customer;
    }
}