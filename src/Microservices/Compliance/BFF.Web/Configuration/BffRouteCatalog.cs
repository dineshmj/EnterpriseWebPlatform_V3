namespace EnterpriseWebPlatform.Compliance.Bff.Web.Configuration;

public static class BffRouteCatalog
{
    // The work queue is the path seeded in EwpBssShellDb.sql ("Compliance Monitor");
    // the case page is where a 401 returns the officer to. Keeping the list here
    // prevents the silent-login endpoint from becoming an open redirector.
    public const string WorkQueue = "/v1/compliance/view-all";
    public const string CaseDetails = "/v1/compliance/cases/view-details";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
        => returnUrl?.TrimEnd('/') is WorkQueue or CaseDetails;
}