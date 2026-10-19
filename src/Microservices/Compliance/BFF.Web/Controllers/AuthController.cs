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
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the return URL is not one of this MFE's own pages, refuse it - a crafted link
        // │ cannot bounce a signed-in person to another site (open redirect).
        // └──────────────────────────────────────────────────────────────────────────────────────
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
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Silent sign-in (prompt=none) inside the Shell's frame: the IDP's single sign-on
        // │ answers without a page; if there is no IDP session, the frame shows "signed out".
        // └──────────────────────────────────────────────────────────────────────────────────────
        properties.Items["prompt"] = "none";

        return Challenge(properties, "oidc");
    }

    /// <summary>
    /// Step-up for a risky action: opened by the Shell in its in-page overlay (in=shell), or in a
    /// small window when the MFE runs outside the Shell. prompt=login + acr_values=mfa: the IDP asks
    /// the signed-in person only for a fresh authenticator code (its step-up code page is the only IDP
    /// page the Shell may frame), and this BFF's session gets tokens with a new auth_time.
    /// </summary>
    [HttpGet("step-up")]
    [Authorize]
    public IActionResult StepUp([FromQuery(Name = "in")] string? host = null)
    {
        // In the Shell's overlay ("in=shell") the end is the Shell's own done page; in a window, this BFF's.
        var properties = new AuthenticationProperties { RedirectUri = host == "shell" ? $"{StepUpDonePath}?in=shell" : StepUpDonePath };
        properties.Items["prompt"] = "login";
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Step-up: prompt=login + acr_values=mfa - the IDP asks the signed-in person only for a
        // │ fresh code; the frame or window returns to the fixed step-up-done address.
        // └──────────────────────────────────────────────────────────────────────────────────────
        properties.Items["acr_values"] = "mfa";
        return Challenge(properties, "oidc");
    }

    public const string StepUpDonePath = "/api/auth/step-up-done";

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ In the Shell's overlay the step-up ends on the Shell's OWN fixed page (configured
    // │ ShellOrigin + /step-up-done.html - never an address from the request): the Shell can
    // │ read its own page's address and closes the overlay. A forged "done" gains nothing -
    // │ the action is retried once and the API checks the fresh MFA itself.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("step-up-done")]
    [Authorize]
    public IActionResult StepUpDone([FromQuery(Name = "in")] string? host, [FromServices] IConfiguration configuration)
    {
        if (host == "shell")
        {
            var shellOrigin = configuration["ShellOrigin"] ?? EnterpriseWebPlatform.Common.Landscape.BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL;
            return Redirect($"{new Uri(shellOrigin).GetLeftPart(UriPartial.Authority)}/step-up-done.html");
        }
        return new ContentResult
        {
            ContentType = "text/html; charset=utf-8",
            Content = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Confirmed</title></head>" +
                      "<body><p>Confirmed. You can close this window; the action continues.</p></body></html>"
        };
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