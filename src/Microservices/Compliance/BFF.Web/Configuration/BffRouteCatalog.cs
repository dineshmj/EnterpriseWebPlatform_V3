using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.Compliance.Bff.Web.Configuration;

public static partial class BffRouteCatalog
{
    // The work queue is the path seeded in EwpBssShellDb.sql ("Compliance Monitor");
    // the case page is where a 401 returns the officer to, and what a notification
    // opens ("?caseId=<number>", nothing else). Keeping the list here
    // prevents the silent-login endpoint from becoming an open redirector.
    public const string WorkQueue = "/v1/compliance/view-all";
    public const string CaseDetails = "/v1/compliance/cases/view-details";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
    {
        if (returnUrl is null) return false;
        if (returnUrl.TrimEnd('/') is WorkQueue or CaseDetails) return true;

        var match = PathWithId().Match(returnUrl);
        return match.Success && match.Groups["path"].Value == CaseDetails && match.Groups["name"].Value == "caseId";
    }

    [GeneratedRegex(@"^(?<path>/[a-z0-9/-]+?)/?\?(?<name>[A-Za-z]+)=\d{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathWithId();
}