using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>
/// The customers' channel: its Journey API, and the customer's OWN data in the domain APIs
/// (read only). Only the customer clients may ask for them; the domain APIs take the customer
/// number from the token, never from the request.
/// </summary>
public static class SelfServiceApiScope
{
    public static ApiScope JourneyRead =>
        new(
                "self-service-journey.read",
                "Self-Service Journey API - Read")
        {
            UserClaims = { "name", "customer_number" }
        };

    public static ApiScope AccountsCustomerRead =>
        new(
                "accounts.customer.read",
                "Accounts API - the customer's own accounts")
        {
            UserClaims = { "customer_number" }
        };

    public static ApiScope PartnerProductsCustomerRead =>
        new(
                "partner-products.customer.read",
                "Partner Products API - the customer's own Digital Gold")
        {
            UserClaims = { "customer_number" }
        };

    /// <summary>Customer Onboarding, for the IDP itself during an online registration.</summary>
    public static ApiScope CustomerOnboardingOnlineIdentity =>
        new(
                "customer-onboarding.online-identity",
                "Customer Onboarding API - online registration checks (IDP only)");
}