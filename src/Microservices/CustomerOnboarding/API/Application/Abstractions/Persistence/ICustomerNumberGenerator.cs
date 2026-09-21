using System.Threading;
using System.Threading.Tasks;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface ICustomerNumberGenerator
{
    Task<long> GetNextAsync(
        CancellationToken cancellationToken);
}
