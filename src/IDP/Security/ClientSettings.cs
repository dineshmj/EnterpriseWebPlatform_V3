using EnterpriseWebPlatform.Common.WebUtilities.Configuration;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: Client IDs and addresses from configuration ────────────────────
// │ Why : The IDP is where every client is registered. Its client IDs and the addresses its
// │       redirect / logout URIs are built from differ per environment; compiled into a shared
// │       library, a change meant rebuilding every component.
// │ How : "ClientIds:<client>" and "ClientBaseUrls:<client>" in this deployable's configuration
// │       (appsettings.json). The client list is built at start-up, so a missing ID, or an address
// │       that is not https, stops the IDP. The components repeat the same values in their own
// │       settings; each such setting says which key here it must equal.
// │ Ref : Blueprint §7 (independent deployability) | OWASP A05 | RFC 6749 §3.1.2 (redirect URIs)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The client IDs ("ClientIds:&lt;client&gt;") and web clients' base addresses
/// ("ClientBaseUrls:&lt;client&gt;") the IDP registers, from its own configuration.
/// </summary>
public static class ClientSettings
{
    private static IConfiguration? _configuration;
    private static ClientIds? _ids;

    public static void Initialize(IConfiguration configuration)
    {
        _configuration = configuration;
        _ids = ClientIds.From(configuration);
    }

    public static ClientIds Ids =>
        _ids ?? throw new InvalidOperationException("ClientSettings has not been initialized.");

    /// <summary>Whether a base address is configured for this client (an optional client exists only then).</summary>
    public static bool HasBaseUrl(string client)
    {
        var configuration = _configuration
            ?? throw new InvalidOperationException("ClientSettings has not been initialized.");
        return !string.IsNullOrWhiteSpace(configuration[$"ClientBaseUrls:{client}"]);
    }

    /// <summary>The client's https base address (no trailing slash), e.g. for its redirect URIs.</summary>
    public static string BaseUrlOf(string client)
    {
        var configuration = _configuration
            ?? throw new InvalidOperationException("ClientSettings has not been initialized.");
        return RequiredSetting.HttpsUrl(configuration[$"ClientBaseUrls:{client}"], $"ClientBaseUrls:{client}");
    }
}