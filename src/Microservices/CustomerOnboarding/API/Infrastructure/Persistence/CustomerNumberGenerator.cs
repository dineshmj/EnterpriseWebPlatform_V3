using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

public sealed class CustomerNumberGenerator : ICustomerNumberGenerator
{
    private readonly CustomerDbContext _dbContext;

    public CustomerNumberGenerator(CustomerDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<long> GetNextAsync(
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldCloseConnection =
            connection.State != ConnectionState.Open;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText =
                "SELECT nextval('public.customer_number_seq');";

            var result =
                await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt64(result);
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
