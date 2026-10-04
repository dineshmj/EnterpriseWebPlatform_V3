using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

namespace EnterpriseWebPlatform.Compliance.Api.Authorization;

/// <summary>
/// RBAC + ABAC requirement for acting on compliance cases: role compliance_officer,
/// department COMPLIANCE, clearance level 3 or above, and EVERY listed permission.
///
/// The case-level rules follow inside the aggregate: branch scope, ReBAC (assigned
/// officer), Separation of Duties across contexts, and the clearance the case's risk
/// requires for approval (LOW 3 / MEDIUM 4 / HIGH 5).
/// </summary>
public sealed class ComplianceOfficerRequirement(params string[] requiredPermissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> RequiredPermissions { get; } = requiredPermissions;
}

public sealed class ComplianceOfficerAuthorizationHandler : AuthorizationHandler<ComplianceOfficerRequirement>
{
    public const int MinimumClearance = 3;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ComplianceOfficerRequirement requirement)
    {
        var user = context.User;
        var permissions = user.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        if (user.IsInRole("compliance_officer") &&
            requirement.RequiredPermissions.All(permissions.Contains) &&
            user.Claims.Any(c => c.Type == "department" && string.Equals(c.Value, "COMPLIANCE", StringComparison.OrdinalIgnoreCase)) &&
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