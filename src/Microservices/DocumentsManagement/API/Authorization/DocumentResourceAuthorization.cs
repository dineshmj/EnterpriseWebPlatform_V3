using System.Security.Claims;
using System.Text.Json;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Authorization;

// ┌─ :: SECURITY :: Branch from the token, acting client checked ──────────────────────────────────
// │ Why : A branch asserted in a header, or any client holding the scope, could read another
// │       branch's evidence.
// │ How : Only a person's exchanged token is accepted, with the CO BFF, the KYC BFF or the Partner
// │       Products Journey API as acting client; the branch comes from that token; only the CO BFF
// │       and the Partner Products Journey API may delete (and only what is not yet retained).
// │ Ref : Features 2.1.2, 2.2.3 | OWASP API1 BOLA | RFC 8693 | AI review 1 fix
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Object-level (branch-scoped) authorization for documents.
///
/// Every call carries a PERSON: the Customer Onboarding and KYC BFFs (and the Partner Products
/// Journey API, for the scan of a customer's signed Form VII or transfer slip) exchange the signed-in
/// user's token for a Documents Management token (OAuth 2.0 Token Exchange, RFC 8693). That
/// token keeps the user as subject, with their own "branch" claim issued by the IDP, and names
/// the BFF in "act". So the branch comes from the IDP, never from a request header, and the
/// acting application is known:
///
///   - a person acting through the Customer Onboarding or KYC BFF, or the Partner Products
///     Journey API: their own branch;
///   - a person's token without one of those BFFs as the acting client (signed in elsewhere,
///     or requested directly): no branch, so denied;
///   - a machine token (no person): no branch, so denied. The Document Invalidation
///     Subscriber uses only its own pinned internal endpoints.
///
/// Only the Customer Onboarding BFF (cleanup of an unfinished submission) and the Partner Products
/// Journey API (a scan whose record the Partner Products API then refused) remove documents.
/// </summary>
public sealed class DocumentResourceAuthorization(ClientIds clientIds)
{
    // The client IDs come from configuration (ClientIds; they must equal the IDP's registrations).
    private readonly HashSet<string> ActingClients = new(StringComparer.Ordinal)
    {
        clientIds.CustomerOnboardingWeb,
        clientIds.CustomerKycWeb,
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The scans of a customer's signed Partner Bank forms (Form VII, transfer slips): the
        // │ Journey API uploads them for the staff member at the counter - their branch, their
        // │ token; it never reads other documents through this rule's branch scope beyond that.
        // └──────────────────────────────────────────────────────────────────────────────────────
        clientIds.PartnerProductsJourneyApi
    };

    private readonly HashSet<string> DeletingClients = new(StringComparer.Ordinal)
    {
        clientIds.CustomerOnboardingWeb,
        // A scan uploaded a moment ago whose record was refused (e.g. a second Form VII): removed
        // again, so no evidence is left attached to nothing; retained documents stay (409).
        clientIds.PartnerProductsJourneyApi
    };

    /// <summary>The acting person's branch, or null (fail closed).</summary>
    public string? GetActorBranch(ClaimsPrincipal user)
    {
        if (user.FindFirst("sub") is null)
            return null;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the token was not exchanged by the CO or KYC BFF, there is no branch - nothing is
        // │ accessible (fail closed).
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (ActingClientOf(user) is not { } actingClient || !ActingClients.Contains(actingClient))
            return null;

        var branch = user.FindFirst("branch")?.Value;

        return string.IsNullOrWhiteSpace(branch)
            ? null
            : branch.Trim().ToUpperInvariant();
    }

    public bool CanAccess(Document document, ClaimsPrincipal user) =>
        BranchCode.TryCreate(GetActorBranch(user), out var actorBranch) &&
        document.BelongsTo(actorBranch);

    public bool CanDelete(Document document, ClaimsPrincipal user) =>
        ActingClientOf(user) is { } actingClient &&
        DeletingClients.Contains(actingClient) &&
        CanAccess(document, user);

    /// <summary>The client acting for the person (the outermost "act" of a delegated token), or null.</summary>
    private static string? ActingClientOf(ClaimsPrincipal user)
    {
        if (user.FindFirst("act")?.Value is not { } act)
            return null;

        try
        {
            using var document = JsonDocument.Parse(act);
            return document.RootElement.TryGetProperty("client_id", out var client) ? client.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}