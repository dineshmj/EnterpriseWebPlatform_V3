using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EnterpriseWebPlatform.IdentityServer;

// ┌─ :: SECURITY :: Headers of the sign-in pages ──────────────────────────────────────────────────
// │ Why : A framed sign-in page can be overlaid to capture a password (clickjacking); inline script
// │       would make an XSS bug exploitable.
// │ How : X-Frame-Options DENY and frame-ancestors 'none'; a CSP without 'unsafe-inline'; nosniff;
// │       no referrer.
// │ Ref : Features 2.1.3, 2.3.2 | OWASP A05 | CWE-1021 (clickjacking) |
// │       OWASP Clickjacking Defense Cheat Sheet
// └────────────────────────────────────────────────────────────────────────────────────────────────
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