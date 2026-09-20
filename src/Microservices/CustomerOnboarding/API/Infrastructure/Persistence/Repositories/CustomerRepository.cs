using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly CustomerDbContext _dbContext;

    public CustomerRepository(CustomerDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Customer?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Customers
            .Include(x => x.Addresses)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public Task<Customer?> GetBySubjectIdAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Customers
            .Include(x => x.Addresses)
            .SingleOrDefaultAsync(
                x => x.SubjectId == subjectId,
                cancellationToken);
    }

    public Task<Customer?> GetByCustomerNumberAsync(
        string customerNumber,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Customers
            .SingleOrDefaultAsync(
                x => x.CustomerNumber.Value == customerNumber,
                cancellationToken);
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
}