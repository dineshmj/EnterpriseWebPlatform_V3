using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Duende.IdentityServer.Services;

namespace EnterpriseWebPlatform.IdentityServer.Pages.Account;

/// <summary>
/// Shown after sign-out. Renders IdentityServer's end-session callback in a
/// hidden iframe, which in turn calls every client's front-channel logout URI
/// (e.g. each BFF's /signout-oidc), so no MFE session survives the logout.
/// </summary>
[SecurityHeaders]
[AllowAnonymous]
public sealed class LoggedOutModel(IIdentityServerInteractionService interaction) : PageModel
{
    public string? PostLogoutRedirectUri { get; private set; }

    public string? ClientName { get; private set; }

    public string? SignOutIframeUrl { get; private set; }

    public async Task<IActionResult> OnGet(string? logoutId)
    {
        var context = await interaction.GetLogoutContextAsync(logoutId, HttpContext.RequestAborted);

        PostLogoutRedirectUri = context?.PostLogoutRedirectUri;
        ClientName = string.IsNullOrEmpty(context?.ClientName) ? context?.ClientId : context.ClientName;
        SignOutIframeUrl = context?.SignOutIFrameUrl;

        return Page();
    }
}
