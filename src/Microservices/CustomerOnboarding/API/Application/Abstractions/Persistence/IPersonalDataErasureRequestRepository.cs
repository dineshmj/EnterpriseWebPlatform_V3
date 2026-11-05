using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface IPersonalDataErasureRequestRepository
{
    Task<PersonalDataErasureRequest?> GetByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>The customer's request still PENDING (expired or not), if any.</summary>
    Task<PersonalDataErasureRequest?> GetPendingForCustomerAsync(long customerId, CancellationToken cancellationToken);

    Task AddAsync(PersonalDataErasureRequest request, CancellationToken cancellationToken);
}

public interface IPersonalDataErasureReadContext
{
    IQueryable<PersonalDataErasureRequest> PersonalDataErasureRequests { get; }
}