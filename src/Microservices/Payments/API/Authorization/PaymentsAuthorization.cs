using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
        var granted = requirement.AnyOfPermissions.Where(permissions.Contains).ToList();

        if (granted.Count == 0 || BranchOf(user) is null)
            return Task.CompletedTask;

        // Auditors read payments only THROUGH the Audit context, where every look is itself
        // recorded: a grant that rests on the auditor's permission alone needs a delegated token
        // whose acting client is the Audit Journey API (token exchange). The Payments screens
        // therefore refuse an auditor, while the Audit Trail's "where it stands now" works.
        if (granted.All(p => p == AuditReadPermission) &&
            ActingClientOf(user) != AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API)
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>The auditor's permission on payments: read-only, all branches, and only through the Audit context.</summary>
    public const string AuditReadPermission = "payment.history.view";

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
        if (user.Claims.Any(c => c.Type == "permission" && AllBranchesPermissions.Contains(c.Value)))
            return (true, null);
        var branch = BranchOf(user);
        return (branch is not null, branch);
    }
}