using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Partner Products context's two APIs. Both receive DELEGATED tokens only (token exchange):
/// the person (role, permissions, LAN ID, branch - the domain API records the actor and limits
/// every list to the actor's branch) plus the acting client chain ("act").
/// </summary>
public sealed class PartnerProductsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "partner-products-api",
                "Partner Products API")
        {
            Scopes = { "partner-products.read", "partner-products.write", "partner-products.customer.read" },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Exactly what the Domain API decides on: the permission (view / order), the branch
            // │ (every record and list is branch-scoped) and the LAN ID it records as the actor.
            // └──────────────────────────────────────────────────────────────────────────────────────
            // customer_number: a customer token (partner-products.customer.read) - whose Digital Gold.
            UserClaims = { "role", "name", "permission", "lan_id", "branch", "customer_number" }
        };

    public static ApiResource JourneyApiResource =>
        new(
                "partner-products-journey-api",
                "Partner Products Journey API")
        {
            Scopes = { "partner-products-journey.read", "partner-products-journey.write" },
            UserClaims = { "role", "permission", "lan_id", "branch" }
        };
}