using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Authorization;

// ┌─ :: SECURITY :: Only a staff member, through the Partner Products Journey API ─────────────────
// │ Why : Recording a customer's signed form starts an account at another bank and, later, a debit
// │       of the customer's account: no machine, and no other client holding the scope, may do it.
// │ How : A DELEGATED token (a person, sub) whose acting client is the Partner Products Journey API;
// │       the scope of the action; the person's permission (partner.gold.view / partner.gold.order)
// │       and branch - every record is the actor's branch's, and every list is limited to it.
// │ Ref : Features 2.1.2, 2.2.2 | RFC 8693 (act) | OWASP API1 BOLA, API5 BFLA
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class DigitalGoldPolicies
{
    public const string View = "DigitalGoldView";
    public const string Order = "DigitalGoldOrder";

    public static AuthorizationOptions AddDigitalGoldPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(View, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_READ)
            .RequireDelegationBy(PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API)
            .RequireClaim("permission", "partner.gold.view")
            .RequireClaim("branch"));

        options.AddPolicy(Order, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_WRITE)
            .RequireDelegationBy(PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API)
            .RequireClaim("permission", "partner.gold.order")
            .RequireClaim("branch"));

        return options;
    }
}