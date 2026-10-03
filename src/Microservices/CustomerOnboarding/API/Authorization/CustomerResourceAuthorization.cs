using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;

/// <summary>
/// Object-level (branch-scoped) authorization for customers and onboarding
/// applications.
///
/// - operations_administrator, platform_administrator, auditor: global (read).
/// - customer_service_agent: customers whose primary residential address is in
///   the agent's branch city and country (branch_city / branch_country_code claims).
/// - anyone else, or an agent without branch location claims: nothing (fail closed).
/// </summary>
public sealed class CustomerResourceAuthorization(CustomerDbContext db)
{
    public CustomerAccessScope GetScope(ClaimsPrincipal user)
    {
        if (user.IsInRole("operations_administrator") ||
            user.IsInRole("platform_administrator") ||
            user.IsInRole("auditor"))
        {
            return new CustomerAccessScope(true, null, null, null);
        }

        if (!user.IsInRole("customer_service_agent"))
            return CustomerAccessScope.None;

        var branchCode = user.FindFirst("branch")?.Value;
        var branchCity = user.FindFirst("branch_city")?.Value;
        var branchCountryCode = user.FindFirst("branch_country_code")?.Value;

        if (string.IsNullOrWhiteSpace(branchCode) ||
            string.IsNullOrWhiteSpace(branchCity) ||
            string.IsNullOrWhiteSpace(branchCountryCode))
        {
            return new CustomerAccessScope(false, branchCode, null, null);
        }

        return new CustomerAccessScope(
            false,
            branchCode.Trim(),
            branchCity.Trim(),
            branchCountryCode.Trim().ToUpperInvariant());
    }

    public async Task<bool> CanAccessApplicationAsync(
        ClaimsPrincipal user,
        long applicationId,
        CancellationToken cancellationToken)
    {
        var customerId = await db.OnboardingApplications
            .AsNoTracking()
            .Where(x => x.Id == applicationId)
            .Select(x => (long?)x.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);

        return customerId is not null &&
            await CanAccessCustomerAsync(user, customerId.Value, cancellationToken);
    }

    public Task<bool> CanAccessCustomerAsync(
        ClaimsPrincipal user,
        long customerId,
        CancellationToken cancellationToken) =>
        db.Customers
            .AsNoTracking()
            .Where(x => x.Id == customerId)
            .WithinScope(GetScope(user))
            .AnyAsync(cancellationToken);
}
