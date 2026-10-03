using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;

/// <summary>Outcome of an object-level authorization check.</summary>
public enum ResourceAccess
{
    /// <summary>Outside the caller's scope: answered as 404 so existence is not disclosed.</summary>
    NotFound,

    /// <summary>Visible to the caller, but the caller may not change it: 403.</summary>
    Forbidden,

    Allowed
}

/// <summary>
/// Object-level authorization for customers and onboarding applications.
///
/// ABAC (who may SEE a customer):
/// - operations_administrator, platform_administrator, auditor: global (read).
/// - customer_service_agent: customers whose primary residential address is in
///   the agent's branch city and country (branch_city / branch_country_code claims).
/// - anyone else, or an agent without branch location claims: nothing (fail closed).
///
/// ReBAC (who may CHANGE a customer or its applications):
/// - only the customer's managing agent (the "manages" relationship stored on the
///   Customer aggregate). Other agents of the same branch can read but not change.
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

    /// <summary>The acting human (token subject), or null for a machine caller.</summary>
    public static string? GetActingUserId(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value;

    /// <summary>The acting agent's branch code, or null when the token has none.</summary>
    public static string? GetActingBranch(ClaimsPrincipal user) =>
        user.FindFirst("branch")?.Value is { Length: > 0 } branch ? branch.Trim() : null;

    public async Task<bool> CanAccessApplicationAsync(
        ClaimsPrincipal user,
        long applicationId,
        CancellationToken cancellationToken)
    {
        var customerId = await CustomerOfApplicationAsync(applicationId, cancellationToken);

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

    /// <summary>ABAC (visible) + ReBAC (the caller manages this customer).</summary>
    public async Task<ResourceAccess> CanManageCustomerAsync(
        ClaimsPrincipal user,
        long customerId,
        CancellationToken cancellationToken)
    {
        var managingAgent = await db.Customers
            .AsNoTracking()
            .Where(x => x.Id == customerId)
            .WithinScope(GetScope(user))
            .Select(x => x.ManagingAgentUserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (managingAgent is null)
            return ResourceAccess.NotFound;

        var actingUserId = GetActingUserId(user);

        return actingUserId is not null &&
               string.Equals(managingAgent, actingUserId, StringComparison.OrdinalIgnoreCase)
            ? ResourceAccess.Allowed
            : ResourceAccess.Forbidden;
    }

    public async Task<ResourceAccess> CanManageApplicationAsync(
        ClaimsPrincipal user,
        long applicationId,
        CancellationToken cancellationToken)
    {
        var customerId = await CustomerOfApplicationAsync(applicationId, cancellationToken);

        return customerId is null
            ? ResourceAccess.NotFound
            : await CanManageCustomerAsync(user, customerId.Value, cancellationToken);
    }

    private Task<long?> CustomerOfApplicationAsync(long applicationId, CancellationToken cancellationToken) =>
        db.OnboardingApplications
            .AsNoTracking()
            .Where(x => x.Id == applicationId)
            .Select(x => (long?)x.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);
}
