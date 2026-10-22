using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: Behind a TLS-terminating ingress ───────────────────────────────
// │ Why : In a container the host listens on plain HTTP and the ingress ends TLS. Without the
// │       forwarded scheme, the IDP would publish http:// endpoints, a BFF would send http://
// │       sign-in redirect URIs (refused by the IDP) and per-IP rate limits would see the ingress,
// │       not the person. Trusting X-Forwarded-* from ANY sender would let a caller fake its IP
// │       and scheme.
// │ How : X-Forwarded-For and X-Forwarded-Proto are applied only when the request comes from an
// │       address listed under ForwardedHeaders:KnownNetworks (CIDR) or :KnownProxies (IP), one
// │       hop. Host is NOT taken from a header (the ingress passes the original Host, which
// │       AllowedHosts checks). Nothing listed (local runs) = the headers are ignored.
// │ Ref : doc/Containers.md | ASP.NET Core "Configure to work with proxy servers" |
// │       OWASP A05 | CWE-348
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class ProxyForwardedHeaders
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Applies X-Forwarded-For / X-Forwarded-Proto from the configured proxies only. Call it
    /// FIRST in the pipeline: logging, HSTS, HTTPS redirection, authentication and rate limiting
    /// must all see the original scheme and client address. Start-up fails on an entry that is
    /// not an IP address / CIDR network.
    /// </summary>
    public static WebApplication UseEwpForwardedHeaders(this WebApplication app)
    {
        var section = app.Configuration.GetSection(SectionName);
        var networks = Entries(section, "KnownNetworks").Select(Network).ToList();
        var proxies = Entries(section, "KnownProxies").Select(Proxy).ToList();
        if (networks.Count == 0 && proxies.Count == 0)
            return app;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        // Only the listed ones: the defaults (loopback) are dropped.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        networks.ForEach(options.KnownIPNetworks.Add);
        proxies.ForEach(options.KnownProxies.Add);

        app.UseForwardedHeaders(options);
        return app;
    }

    private static IEnumerable<string> Entries(IConfigurationSection section, string key) =>
        section.GetSection(key).GetChildren()
            .Select(entry => entry.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))!;

    private static System.Net.IPNetwork Network(string value) =>
        System.Net.IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException($"{SectionName}:KnownNetworks entry '{value}' is not a CIDR network (e.g. 10.0.0.0/8).");

    private static IPAddress Proxy(string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"{SectionName}:KnownProxies entry '{value}' is not an IP address.");
}