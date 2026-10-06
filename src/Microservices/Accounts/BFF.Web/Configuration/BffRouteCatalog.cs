using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;

public static partial class BffRouteCatalog
{
    // The three pages seeded in EwpBssShellDb.sql (View Account Applications, View
    // Accounts, Account Lifecycle), plus the application page a 401 returns the officer
    // to and a notification opens ("?applicationId=<number>", nothing else). Keeping the
    // list here prevents the silent-login endpoint from becoming an open redirector.
    public const string WorkQueue = "/v1/accounts/applications/view-all";
    public const string ApplicationDetails = "/v1/accounts/applications/view-details";
    public const string Accounts = "/v1/accounts/view-all";
    public const string Lifecycle = "/v1/accounts/lifecycle/view-all";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
    {
        if (returnUrl is null) return false;
        if (returnUrl.TrimEnd('/') is WorkQueue or ApplicationDetails or Accounts or Lifecycle) return true;

        var match = PathWithId().Match(returnUrl);
        return match.Success && match.Groups["path"].Value == ApplicationDetails && match.Groups["name"].Value == "applicationId";
    }

    [GeneratedRegex(@"^(?<path>/[a-z0-9/-]+?)/?\?(?<name>[A-Za-z]+)=\d{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathWithId();
}