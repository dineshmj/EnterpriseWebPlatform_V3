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
    /// <summary>HttpContext.Items key: the one page the Shell may frame (the step-up code page).</summary>
    public const string ShellMayFrameKey = "ewp.idp.shell-may-frame";

    public override void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not (ViewResult or PageResult))
        {
            return;
        }

        var headers = context.HttpContext.Response.Headers;

        headers.TryAdd("X-Content-Type-Options", "nosniff");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ ONE exception to "never framed": the authenticator-code page of a STEP-UP (a person
        // │ already signed in, confirming a risky action - no password on it) may be framed by
        // │ the Shell's origin only, for the Shell's in-page overlay. The sign-in, enrolment,
        // │ consent and every other page - and the code page of a normal sign-in - stay 'none'.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var frameAncestors = context.HttpContext.Items.ContainsKey(ShellMayFrameKey)
            ? ContentSecurityPolicyOrigin(EnterpriseWebPlatform.Common.Landscape.BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL)
            : "'none'";

        // The login page must never be framed (clickjacking / credential capture).
        if (frameAncestors == "'none'")
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
            $"frame-ancestors {frameAncestors}; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self' https:;");
    }

    private static string ContentSecurityPolicyOrigin(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);
}