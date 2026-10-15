using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: SECURITY :: Delegated tokens: who acts for the person ─────────────────────────────────────
// │ Why : Some endpoints may be called only through one application acting for a signed-in
// │       person - not by that person's own token, a machine, or any other client with the scope.
// │ How : RFC 8693 token exchange names the acting client in the "act" claim; a policy requires a
// │       person (sub) AND exactly the expected acting client. Anything else fails closed.
// │ Ref : Features 2.1.2 | RFC 8693 §4.1 (act) | OWASP API5 BFLA
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class DelegatedTokens
{
    /// <summary>The client acting for the person (the outermost "act"), or null for a token without delegation.</summary>
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
            return null;   // a malformed "act" proves nothing
        }
    }

    /// <summary>Requires a person's token that <paramref name="actingClientId"/> obtained by token exchange.</summary>
    public static AuthorizationPolicyBuilder RequireDelegationBy(this AuthorizationPolicyBuilder policy, string actingClientId) =>
        policy.RequireAssertion(context =>
            context.User.FindFirst("sub") is not null &&
            string.Equals(ActingClientOf(context.User), actingClientId, StringComparison.Ordinal));
}