using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

/// <summary>Next value of public.application_number_seq (unique, gap-tolerant).</summary>
public sealed class ApplicationNumberGenerator(CustomerDbContext dbContext) : IApplicationNumberGenerator
{
    public async Task<long> GetNextAsync(CancellationToken cancellationToken)
    {
        var values = await dbContext.Database
            .SqlQuery<long>($"SELECT nextval('public.application_number_seq') AS \"Value\"")
            .ToListAsync(cancellationToken);

        return values.Single();
    }
}
