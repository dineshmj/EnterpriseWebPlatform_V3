using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EnterpriseWebPlatform.IdentityServer;

/// <summary>
/// Security headers for the IDP's interactive pages (login, consent, logout).
/// Razor Pages return a PageResult, MVC views a ViewResult: both are covered.
/// </summary>
public sealed class SecurityHeadersAttribute
    : ActionFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not (ViewResult or PageResult))
        {
            return;
        }

        var headers = context.HttpContext.Response.Headers;

        headers.TryAdd("X-Content-Type-Options", "nosniff");

        // The login page must never be framed (clickjacking / credential capture).
        headers.TryAdd("X-Frame-Options", "DENY");

        headers.TryAdd("Referrer-Policy", "no-referrer");

        // Everything is self-hosted (no CDN) and the pages use no inline scripts or
        // styles, so no 'unsafe-inline' anywhere.
        // frame-src 'self': the logged-out page embeds the IDP's own end-session
        // callback, which in turn renders the clients' front-channel logout iframes.
        headers.TryAdd(
            "Content-Security-Policy",
            "default-src 'self'; " +
            "style-src 'self'; " +
            "script-src 'self'; " +
            "font-src 'self'; " +
            "frame-src 'self'; " +
            "frame-ancestors 'none'; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self' https:;");
    }
}
