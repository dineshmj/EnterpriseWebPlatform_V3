namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;

public static class BffRouteCatalog
{
    // These are the exact relative paths seeded in EwpBssShellDb.sql.
    // Keeping them in one place prevents the silent-login endpoint from
    // becoming an open redirector.
    public const string Customers = "/v1/customers/view-all";
    public const string Applications = "/v1/onboarding/applications/view-all";
    public const string Workflow = "/v1/onboarding/workflow/view-all";

    public static bool IsAllowedSpaRoute(string? returnUrl)
        => returnUrl is Customers or Applications or Workflow;
}
