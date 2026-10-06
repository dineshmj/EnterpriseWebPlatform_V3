using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Authorization;

/// <summary>
/// RBAC + ABAC requirement for staff acting on payments: at least ONE of the listed
/// permissions, and a branch (the ABAC scope every payment query and command is limited to).
///
/// The payment-level rules follow in the application and the aggregates: branch scope on
/// every read, Idempotency-Key ownership, and (step 5b) approval tiers and Separation of
/// Duties.
/// </summary>
public sealed class PaymentsStaffRequirement(params string[] anyOfPermissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> AnyOfPermissions { get; } = anyOfPermissions;
}

public sealed class PaymentsStaffAuthorizationHandler : AuthorizationHandler<PaymentsStaffRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PaymentsStaffRequirement requirement)
    {
        var user = context.User;
        var permissions = user.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        if (requirement.AnyOfPermissions.Any(permissions.Contains) && BranchOf(user) is not null)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    /// <summary>The user's branch (ABAC); null when absent or malformed (fail closed).</summary>
    public static BranchCode? BranchOf(ClaimsPrincipal user) =>
        BranchCode.TryCreate(user.FindFirst("branch")?.Value, out var branch) ? branch : null;
}