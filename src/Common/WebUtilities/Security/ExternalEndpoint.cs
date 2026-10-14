using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: SECURITY :: Calls to external systems (SSRF by configuration) ─────────────────────────────
// │ Why : The address of core banking, the screening provider or the payment network is
// │       configuration; a wrong or tampered value would send the API key and customer data
// │       wherever it points.
// │ How : Checked at start-up: absolute, no credentials in the URL; outside Development also https,
// │       a host listed in PermittedHosts, and no loopback / private / link-local IP; the clients
// │       follow no redirects.
// │ Ref : Features 2.4.9 | OWASP A10:2021 SSRF | OWASP API7:2023 | CWE-918 | AI review 2: xAI F-6
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The address of an EXTERNAL system (core banking, screening provider, payment network) is
/// configuration, never caller input - yet a wrong or tampered value would send the API key, and
/// customer data, wherever it points (SSRF by configuration, CWE-918). So, at start-up:
/// <list type="bullet">
///   <item>always: an absolute http(s) URL without user name or password;</item>
///   <item>outside Development: https, a host listed in the section's "PermittedHosts", and no
///   loopback, private, link-local or unspecified IP address.</item>
/// </list>
/// And the client never follows a redirect: a 3xx cannot move the call (and its key) elsewhere.
/// </summary>
public static class ExternalEndpoint
{
    /// <summary>Why the base URL is refused; null when it is acceptable.</summary>
    public static string? Problem(string? baseUrl, IReadOnlyCollection<string>? permittedHosts, bool isDevelopment)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "is not an absolute http(s) URL";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "must not carry a user name or password";
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If this is Development, local simulators on localhost are fine. Elsewhere the address
        // │ must be https, a permitted host and no internal IP.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (isDevelopment)
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return "must use https outside Development";
        if (permittedHosts is null || !permittedHosts.Any(h => string.Equals(h, uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            return $"host '{uri.IdnHost}' is not listed in PermittedHosts";
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the host is a loopback, private or link-local address (e.g. the cloud metadata
        // │ endpoint 169.254.169.254), refuse it - SSRF target.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (IPAddress.TryParse(uri.IdnHost, out var address) && IsInternal(address))
            return $"host '{uri.IdnHost}' is a loopback, private or link-local address";
        return null;
    }

    /// <summary>Loopback, private (RFC 1918 / unique local), link-local (incl. cloud metadata 169.254.169.254) or unspecified.</summary>
    public static bool IsInternal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // carrier-grade NAT
    }

    /// <summary>The external client follows no redirects.</summary>
    public static IHttpClientBuilder WithoutRedirects(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
}