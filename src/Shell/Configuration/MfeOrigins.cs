using EnterpriseWebPlatform.Common.WebUtilities.Configuration;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Configuration;

// ┌─ :: ARCHITECTURE + SECURITY :: One list of MFE addresses ──────────────────────────────────────
// │ Why : The Shell frames only the MFEs it knows (CSP frame-src), and the menu sends the browser to
// │       them. Two lists - configuration for the CSP, the menu database for navigation - had to
// │       be kept equal by hand, and the database held one environment's addresses.
// │ How : MfeOrigins (configuration, per environment) is the only list: key -> https origin. The
// │       menu database names each MFE by its key; the menu endpoint takes the address from here.
// │       Every address must be https (checked at start-up); a menu key without an address is
// │       left out of the menu - the Shell never sends anyone to an address the CSP would not frame.
// │ Ref : Features 1.5.1 | Shell-Requirements.md | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The MFEs' origins by key, from the MfeOrigins setting.</summary>
public sealed class MfeOrigins
{
    private readonly IReadOnlyDictionary<string, string> origins;

    private MfeOrigins(IReadOnlyDictionary<string, string> origins) => this.origins = origins;

    /// <summary>Every configured origin (the CSP's frame-src list).</summary>
    public IEnumerable<string> All => origins.Values;

    /// <summary>The origin for a menu entry's key; false when this environment has none.</summary>
    public bool TryGet(string key, out string origin) => origins.TryGetValue(key, out origin!);

    /// <summary>Reads MfeOrigins; a missing list or an address that is not https stops the start-up.</summary>
    public static MfeOrigins From(IConfiguration configuration)
    {
        var origins = configuration.GetSection("MfeOrigins").GetChildren()
            .ToDictionary(
                mfe => mfe.Key,
                mfe => RequiredSetting.HttpsUrl(mfe.Value, $"MfeOrigins:{mfe.Key}").TrimEnd('/'),
                StringComparer.Ordinal);
        if (origins.Count == 0)
            throw new InvalidOperationException("MfeOrigins is not configured: the Shell would frame no MFE.");
        return new MfeOrigins(origins);
    }
}