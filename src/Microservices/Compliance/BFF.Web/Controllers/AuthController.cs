using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Duende.AccessTokenManagement.OpenIdConnect;

using EnterpriseWebPlatform.Compliance.Bff.Web.Configuration;

namespace EnterpriseWebPlatform.Compliance.Bff.Web.Controllers;

// Anonymous only where it must be (signing in, "who am I?"): a class-level
// [AllowAnonymous] would silently cancel [Authorize] on csrf and logout.
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpGet("silent-login")]
    [AllowAnonymous]
    public IActionResult SilentLogin([FromQuery] string returnUrl = BffRouteCatalog.WorkQueue)
    {
        if (!BffRouteCatalog.IsAllowedSpaRoute(returnUrl))
        {
            return BadRequest(new { message = "Invalid Compliance MFE return URL." });
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(returnUrl);
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = returnUrl
        };
        properties.Items["prompt"] = "none";

        return Challenge(properties, "oidc");
    }

    [HttpGet("user")]
    [AllowAnonymous]
    public IActionResult UserInfo()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        // Only what the UI needs for display and hints (branch, clearance); every
        // rule is still enforced by the Compliance API from the access token.
        var displayClaimTypes = new HashSet<string>(StringComparer.Ordinal) { "sub", "name", "role", "branch", "clearance_level" };

        return Ok(User.Claims
            .Where(c => displayClaimTypes.Contains(c.Type))
            .Select(c => new
            {
                type = c.Type,
                value = c.Value
            }));
    }

    [HttpGet("csrf")]
    [Authorize]
    public IActionResult Csrf([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }

    // POST with anti-forgery: a GET logout could be triggered by any third-party page.
    // (The user-facing logout is owned by the Shell; the IDP's front-channel and
    // back-channel logout end this BFF's session.)
    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await HttpContext.RevokeRefreshTokenAsync();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, "oidc");
    }
}