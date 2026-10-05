using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

namespace EnterpriseWebPlatform.Accounts.Api.Authorization;

/// <summary>
/// RBAC + ABAC requirement for acting on account applications and viewing accounts:
/// role account_officer, department ACCOUNTS, clearance level 3 or above, and EVERY
/// listed permission.
///
/// The application-level rules follow inside the aggregate: branch scope, ReBAC
/// (assigned officer) and Separation of Duties across contexts (not the initiator, not
/// the Compliance approver).
/// </summary>
public sealed class AccountOfficerRequirement(params string[] requiredPermissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> RequiredPermissions { get; } = requiredPermissions;
}

public sealed class AccountOfficerAuthorizationHandler : AuthorizationHandler<AccountOfficerRequirement>
{
    public const int MinimumClearance = 3;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AccountOfficerRequirement requirement)
    {
        var user = context.User;
        var permissions = user.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        if (user.IsInRole("account_officer") &&
            requirement.RequiredPermissions.All(permissions.Contains) &&
            user.Claims.Any(c => c.Type == "department" && string.Equals(c.Value, "ACCOUNTS", StringComparison.OrdinalIgnoreCase)) &&
            ClearanceOf(user) >= MinimumClearance)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>The highest clearance_level claim; 0 when absent (fail closed).</summary>
    public static int ClearanceOf(ClaimsPrincipal user) =>
        user.Claims.Where(c => c.Type == "clearance_level")
            .Select(c => int.TryParse(c.Value, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();
}