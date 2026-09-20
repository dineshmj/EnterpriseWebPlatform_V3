using System.Linq;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface ICustomerReadContext
{
    IQueryable<Customer> Customers { get; }
}