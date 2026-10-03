using Microsoft.AspNetCore.Authorization;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Authorization;

/// <summary>
/// RBAC + ABAC requirement for acting on a KYC case: role kyc_officer, department
/// KYC, clearance level 3 or above, and EVERY listed permission (e.g. the decision
/// permission plus the stage permission kyc.identity.verify / kyc.document.verify).
///
/// The resource-level rules are enforced after this, per case: branch scope (the
/// officer's branch must be the case's branch), ReBAC (only the assigned officer
/// decides) and Separation of Duties (never the workflow initiator).
/// </summary>
public sealed class KycCaseDecisionRequirement(params string[] requiredPermissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> RequiredPermissions { get; } = requiredPermissions;
}

public sealed class KycCaseDecisionAuthorizationHandler
    : AuthorizationHandler<KycCaseDecisionRequirement>
{
    public const int MinimumClearance = 3;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        KycCaseDecisionRequirement requirement)
    {
        var user = context.User;

        var permissions = user.Claims
            .Where(c => c.Type == "permission")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        var isKycOfficer = user.IsInRole("kyc_officer");
        var hasPermissions = requirement.RequiredPermissions.All(permissions.Contains);
        var isKycDepartment = user.Claims.Any(c =>
            c.Type == "department" &&
            string.Equals(c.Value, "KYC", StringComparison.OrdinalIgnoreCase));

        var clearance = user.Claims
            .Where(c => c.Type == "clearance_level")
            .Select(c => int.TryParse(c.Value, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        if (isKycOfficer && hasPermissions && isKycDepartment && clearance >= MinimumClearance)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
