using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: SECURITY :: Deny by default ───────────────────────────────────────────────────────────────
// │ Why : A controller added later without an [Authorize] attribute would otherwise be public.
// │ How : Every domain API maps its controllers through MapEwpApiControllers (an authenticated
// │       caller on top of each action's own policy) and sets a fallback policy, so any other
// │       endpoint needs one too; only health and metrics are explicitly anonymous.
// │ Ref : Features 2.2.1 | OWASP API5:2023 Broken Function Level Authorization | OWASP A01 |
// │       ASVS V4.1.3 | AI review 2: xAI F-1, F-5
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Deny by default, the same way in every domain API (OWASP API5, ASVS V4.1.3):
/// <list type="bullet">
///   <item>FALLBACK policy: an endpoint with no authorization metadata of its own needs an
///   authenticated caller. Endpoints meant to be open say so explicitly (health and metrics:
///   AllowAnonymous).</item>
///   <item>Controllers: an authenticated caller, plus the given policies, on top of every
///   action's own [Authorize(Policy = ...)].</item>
/// </list>
/// A controller added later without any attribute is therefore never public.
/// </summary>
public static class ApiEndpointAuthorization
{
    /// <summary>Endpoints without authorization metadata need an authenticated caller.</summary>
    public static AuthorizationOptions DenyAnonymousByDefault(this AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        return options;
    }

    /// <summary>All controllers, each requiring an authenticated caller and every given policy.</summary>
    public static ControllerActionEndpointConventionBuilder MapEwpApiControllers(this IEndpointRouteBuilder endpoints, params string[] policies)
    {
        var controllers = endpoints.MapControllers();
        return policies.Length == 0
            ? controllers.RequireAuthorization()
            : controllers.RequireAuthorization(policies);
    }
}