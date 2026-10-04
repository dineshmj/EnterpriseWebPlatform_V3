namespace EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;

public sealed class CustomerOnboardingBffOptions
{
    public const string SectionName = "CustomerOnboardingBff";

    public string SpaBaseUrl { get; init; } = "https://customer.dev.localhost:44311";
    public string CustomerOnboardingApiBaseUrl { get; init; } = "https://customer-api.dev.localhost:44363";
    public string DocumentsManagementApiBaseUrl { get; init; } = "https://documents-management-api.dev.localhost:49486";
    public string IdentityServerAuthority { get; init; } = "https://idp.dev.localhost:44392";
    public string M2MClientId { get; init; } = string.Empty;
    public string M2MClientSecret { get; init; } = string.Empty;
    public int M2MTokenRefreshSkewSeconds { get; init; } = 60;
}