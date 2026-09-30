using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task AddAsync(Customer customer, CancellationToken cancellationToken);
}