using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Duende.AccessTokenManagement.OpenIdConnect;
using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpGet("silent-login")]
    public IActionResult SilentLogin([FromQuery] string returnUrl = BffRouteCatalog.Customers)
    {
        if (!BffRouteCatalog.IsAllowedSpaRoute(returnUrl))
        {
            return BadRequest(new { message = "Invalid Customer Onboarding MFE return URL." });
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
    public IActionResult UserInfo()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        return Ok(User.Claims.Select(c => new
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

    [HttpGet("logout")]
    [Authorize]
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
