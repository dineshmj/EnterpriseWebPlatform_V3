using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.Audit.Api.Authorization;

/// <summary>
/// Who may read the audit trail - all of it must hold, or the answer is 403 (fail closed):
///  - the token is a DELEGATED one: its "act" claim names the Audit Journey API as the acting
///    client (it was obtained by token exchange, RFC 8693). A plain machine token, the web BFF's
///    token, or a person's own token is refused - only the Journey API may call, and only for
///    a person;
///  - it carries the scope audit.read;
///  - the PERSON (sub) holds the permission - audit.view, and audit.search for searching -
///    which only the auditor role has.
/// </summary>
public sealed record DelegatedAuditorRequirement(string Permission) : IAuthorizationRequirement;

public sealed class DelegatedAuditorAuthorizationHandler : AuthorizationHandler<DelegatedAuditorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DelegatedAuditorRequirement requirement)
    {
        var user = context.User;
        if (user.FindFirstValue("sub") is not null &&
            HasScope(user, AuditApiScopesRequired.AUDIT_READ) &&
            ActingClientOf(user) == AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API &&
            user.HasClaim("permission", requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>The client acting for the person (the outermost "act"), or null for a non-delegated token.</summary>
    public static string? ActingClientOf(ClaimsPrincipal user)
    {
        if (user.FindFirstValue("act") is not { } act)
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

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").Any(c => c.Value.Split(' ').Contains(scope));
}