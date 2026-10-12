using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;

public static partial class BffRouteCatalog
{
    // These are the exact relative paths seeded in EwpBssShellDb.sql, plus the applications
    // list a progress notification opens ("?applicationNumber=APP-yyyyMMdd-nnnnnn", nothing
    // else). Keeping them in one place prevents the silent-login endpoint from becoming an
    // open redirector.
    public const string Customers = "/v1/customers/view-all";
    public const string NewApplication = "/v1/onboarding/applications/new";
    public const string Applications = "/v1/onboarding/applications/view-all";
    public const string Workflow = "/v1/onboarding/workflow/view-all";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
    {
        if (returnUrl is null) return false;
        if (returnUrl.TrimEnd('/') is Customers or NewApplication or Applications or Workflow) return true;

        var match = ApplicationsWithNumber().Match(returnUrl);
        return match.Success && match.Groups["path"].Value == Applications;
    }

    [GeneratedRegex(@"^(?<path>/[a-z0-9/-]+?)/?\?applicationNumber=APP-\d{8}-\d{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationsWithNumber();
}