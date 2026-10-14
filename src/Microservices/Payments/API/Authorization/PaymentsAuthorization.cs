using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Authorization;

// ┌─ :: SECURITY :: Staff authorization: permission + branch (RBAC + ABAC) ────────────────────────
// │ Why : A role alone must not open another branch's payments, and an auditor's all-branches
// │       permission must not leak into the operational screens.
// │ How : Each policy needs one of its permissions AND a branch claim; reads are limited to the
// │       person's branch except for operations and audit; the auditor's permission counts only
// │       when the Audit Journey API is the acting client.
// │ Ref : Features 2.2.3, 2.2.4 | OWASP API1:2023 BOLA | Authorization-Model 5, 10 |
// │       AI review 1 fix R4
// └────────────────────────────────────────────────────────────────────────────────────────────────
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
        var permissions = EffectivePermissionsOf(user);

        if (!requirement.AnyOfPermissions.Any(permissions.Contains) || BranchOf(user) is null)
            return Task.CompletedTask;

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>The auditor's permission on payments: read-only, all branches, and only through the Audit context.</summary>
    public const string AuditReadPermission = "payment.history.view";

    /// <summary>
    /// The person's permissions as they count HERE. Auditors read payments only THROUGH the
    /// Audit context, where every look is itself recorded: the auditor's permission counts only
    /// in a delegated token whose acting client is the Audit Journey API (token exchange). It is
    /// dropped otherwise - also for a person holding it next to another role, who must not gain
    /// the auditor's all-branches reach on the Payments screens.
    /// </summary>
    public static IReadOnlySet<string> EffectivePermissionsOf(ClaimsPrincipal user)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The auditor's permission counts only through the Audit Journey API (token exchange) -
        // │ anywhere else it is dropped, even for a person who also holds another role.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var viaAuditContext = ActingClientOf(user) == AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API;
        return user.Claims
            .Where(c => c.Type == "permission" && (viaAuditContext || c.Value != AuditReadPermission))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The client acting for the person (the outermost "act" of a delegated token), or null.</summary>
    public static string? ActingClientOf(ClaimsPrincipal user)
    {
        if (user.FindFirst("act")?.Value is not { } act)
            return null;
        try
        {
            using var document = JsonDocument.Parse(act);
            return document.RootElement.TryGetProperty("client_id", out var client) ? client.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The user's branch (ABAC); null when absent or malformed (fail closed).</summary>
    public static BranchCode? BranchOf(ClaimsPrincipal user) =>
        BranchCode.TryCreate(user.FindFirst("branch")?.Value, out var branch) ? branch : null;

    /// <summary>Permissions whose work is not branch-bound: operations (workflow.view) and audit (payment.history.view, through the Audit context only).</summary>
    public static readonly IReadOnlySet<string> AllBranchesPermissions =
        new HashSet<string>(StringComparer.Ordinal) { "workflow.view", "payment.history.view" };

    /// <summary>
    /// The branch a read is limited to: none (all branches) for operations and auditors, the
    /// user's own branch for everyone else.
    /// </summary>
    public static (bool Allowed, BranchCode? Branch) ReadScopeOf(ClaimsPrincipal user)
    {
        if (EffectivePermissionsOf(user).Overlaps(AllBranchesPermissions))
            return (true, null);
        var branch = BranchOf(user);
        return (branch is not null, branch);
    }
}