namespace EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

public static class PartnerProductsApiScopesRequired
{
    /// <summary>Read Partner Products records (Domain API) - only in a token exchanged by the Partner Products Journey API.</summary>
    public const string PARTNER_PRODUCTS_READ =
        "partner-products.read";

    /// <summary>Record a customer's signed Form VII or transfer slip (Domain API) - only in a token exchanged by the Journey API.</summary>
    public const string PARTNER_PRODUCTS_WRITE =
        "partner-products.write";

    /// <summary>Call the Partner Products Journey API to read - only in a token exchanged by the Partner Products web BFF.</summary>
    public const string PARTNER_PRODUCTS_JOURNEY_READ =
        "partner-products-journey.read";

    /// <summary>Call the Partner Products Journey API to record a form - only in a token exchanged by the web BFF.</summary>
    public const string PARTNER_PRODUCTS_JOURNEY_WRITE =
        "partner-products-journey.write";
}