namespace EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;

public static class BffRouteCatalog
{
    // The three pages seeded in EwpBssShellDb.sql (View Account Applications, View
    // Accounts, Account Lifecycle), plus the application page a 401 returns the officer
    // to. Keeping the list here prevents the silent-login endpoint from becoming an open
    // redirector.
    public const string WorkQueue = "/v1/accounts/applications/view-all";
    public const string ApplicationDetails = "/v1/accounts/applications/view-details";
    public const string Accounts = "/v1/accounts/view-all";
    public const string Lifecycle = "/v1/accounts/lifecycle/view-all";

    /// <summary>True for an allowed MFE path, with or without the trailing slash of the static export.</summary>
    public static bool IsAllowedSpaRoute(string? returnUrl)
        => returnUrl?.TrimEnd('/') is WorkQueue or ApplicationDetails or Accounts or Lifecycle;
}