using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: The MFE's Shell origin, set at run time ─────────────────────────
// │ Why : The Shell origin is the only window an MFE exchanges messages with. Compiled into the
// │       static export, it made every BFF image specific to one environment; one image must run
// │       everywhere, with the environment saying where the Shell is.
// │ How : The BFF serves /ewp-runtime-config.js from its own ShellOrigin setting (already
// │       validated as https at start-up); the export loads it as a beforeInteractive script, so
// │       Next.js has it before it starts the page.
// │       The file is same origin (CSP script-src 'self'); Next.js's one-line inline loader for it
// │       is hashed at start-up like the export's other inline scripts.
// │       Not cached (no-cache), so a changed setting applies on the next page load.
// │ Ref : doc/Containers.md | Features 1.5.2, 2.3.2 | HTML postMessage security | CWE-346
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Serves <c>/ewp-runtime-config.js</c>: <c>window.__EWP_RUNTIME__ = { shellOrigin }</c>, frozen.
/// Each MFE's <c>shell-origin.ts</c> reads it; without it the MFE exchanges no message with any
/// window (fail closed).
/// </summary>
public static class MfeRuntimeConfig
{
    public const string Path = "/ewp-runtime-config.js";

    public static IEndpointConventionBuilder MapEwpMfeRuntimeConfig(this IEndpointRouteBuilder endpoints, string shellOrigin)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Built once, from configuration only (never from the request), and JSON-encoded: the
        // │ value cannot break out of the string literal.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var settings = JsonSerializer.Serialize(new { shellOrigin = ContentSecurityPolicy.Origin(shellOrigin) });
        var script = $"window.__EWP_RUNTIME__ = Object.freeze({settings});";

        return endpoints.MapGet(Path, (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "no-cache";
                return Results.Text(script, "text/javascript; charset=utf-8");
            })
            // The page loads it before anyone signs in; it holds no secret.
            .AllowAnonymous();
    }
}