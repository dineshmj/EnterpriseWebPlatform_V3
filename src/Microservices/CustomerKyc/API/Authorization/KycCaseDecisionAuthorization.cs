using Microsoft.AspNetCore.Authorization;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Authorization;

public sealed class KycCaseDecisionRequirement(string requiredPermission) : IAuthorizationRequirement
{
    public string RequiredPermission { get; } = requiredPermission;
}

public sealed class KycCaseDecisionAuthorizationHandler
    : AuthorizationHandler<KycCaseDecisionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        KycCaseDecisionRequirement requirement)
    {
        var user = context.User;

        var isKycOfficer = user.IsInRole("kyc_officer");
        var hasPermission = user.Claims.Any(c =>
            c.Type == "permission" &&
            string.Equals(c.Value, requirement.RequiredPermission, StringComparison.Ordinal));
        var isKycDepartment = user.Claims.Any(c =>
            c.Type == "department" &&
            string.Equals(c.Value, "KYC", StringComparison.OrdinalIgnoreCase));

        var clearance = user.Claims
            .Where(c => c.Type == "clearance_level")
            .Select(c => int.TryParse(c.Value, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        // The current KYC decision rule requires clearance level 3 or above.
        // Resource/branch scope is deliberately not inferred here because the
        // current KycCase aggregate has no branch ownership field.
        if (isKycOfficer && hasPermission && isKycDepartment && clearance >= 3)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
