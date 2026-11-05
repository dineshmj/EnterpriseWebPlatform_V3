using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;

public sealed class PersonalDataErasureRequestRepository(CustomerDbContext dbContext) : IPersonalDataErasureRequestRepository
{
    public Task<PersonalDataErasureRequest?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        dbContext.PersonalDataErasureRequests.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<PersonalDataErasureRequest?> GetPendingForCustomerAsync(long customerId, CancellationToken cancellationToken) =>
        dbContext.PersonalDataErasureRequests.SingleOrDefaultAsync(
            x => x.CustomerId == customerId && x.Status == PersonalDataErasureStatus.Pending,
            cancellationToken);

    public async Task AddAsync(PersonalDataErasureRequest request, CancellationToken cancellationToken) =>
        await dbContext.PersonalDataErasureRequests.AddAsync(request, cancellationToken);
}