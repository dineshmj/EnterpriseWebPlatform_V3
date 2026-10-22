namespace EnterpriseWebPlatform.IdentityServer.Customers;

/// <summary>Configuration "CustomerSignIn": the customers' own sign-in and online registration.</summary>
public sealed class CustomerSignInOptions
{
    public const string SectionName = "CustomerSignIn";

    /// <summary>Customer Onboarding API (https): checks a registration's details and links the new sign-in.</summary>
    public string CustomerOnboardingApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Development only: show a one-time code on the page, in place of the SMS the PoC cannot
    /// send. The IDP refuses to start with this on in any other environment.
    /// </summary>
    public bool ShowCodesOnPage { get; init; }
}