using Microsoft.AspNetCore.Authorization;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
    public const string Worker = "DigitalGoldWorker";

    /// <param name="clientIds">The Journey API's and the worker's client IDs (configuration, as the IDP registered them).</param>
    public static AuthorizationOptions AddDigitalGoldPolicies(this AuthorizationOptions options, ClientIds clientIds)
    {
        options.AddPolicy(View, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", "partner-products.read")
            .RequireDelegationBy(clientIds.PartnerProductsJourneyApi)
            .RequireClaim("permission", "partner.gold.view")
            .RequireClaim("branch"));

        options.AddPolicy(Order, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", "partner-products.write")
            .RequireDelegationBy(clientIds.PartnerProductsJourneyApi)
            .RequireClaim("permission", "partner.gold.order")
            .RequireClaim("branch"));

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The purchase workflow's follow-up (payment advice, cancellation): ONLY the Partner
        // │ Products Worker's own machine token - its client ID pinned, no person (sub) in it.
        // │ The staff policies above never accept it (no delegation, no permission, no branch).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.AddPolicy(Worker, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("scope", "partner-products.write")
            .RequireClaim("client_id", clientIds.PartnerProductsWorker)
            .RequireAssertion(context => !context.User.HasClaim(c => c.Type is "sub" or "act")));

        return options;
    }
}