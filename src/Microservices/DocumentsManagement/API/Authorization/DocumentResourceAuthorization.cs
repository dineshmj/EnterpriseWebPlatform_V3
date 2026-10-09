using System.Security.Claims;
using System.Text.Json;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Authorization;

/// <summary>
/// Object-level (branch-scoped) authorization for documents.
///
/// Every call carries a PERSON: the Customer Onboarding and KYC BFFs exchange the signed-in
/// user's token for a Documents Management token (OAuth 2.0 Token Exchange, RFC 8693). That
/// token keeps the user as subject, with their own "branch" claim issued by the IDP, and names
/// the BFF in "act". So the branch comes from the IDP, never from a request header, and the
/// acting application is known:
///
///   - a person acting through the Customer Onboarding or KYC BFF: their own branch;
///   - a person's token without one of those BFFs as the acting client (signed in elsewhere,
///     or requested directly): no branch, so denied;
///   - a machine token (no person): no branch, so denied. The Document Invalidation
///     Subscriber uses only its own pinned internal endpoints.
///
/// Only the Customer Onboarding BFF removes documents (cleanup of an unfinished submission).
/// </summary>
public sealed class DocumentResourceAuthorization
{
    private static readonly HashSet<string> ActingClients = new(StringComparer.Ordinal)
    {
        CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP,
        CustomerKycMicroservice.CLIENT_ID_FOR_IDP
    };

    private static readonly HashSet<string> DeletingClients = new(StringComparer.Ordinal)
    {
        CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP
    };

    /// <summary>The acting person's branch, or null (fail closed).</summary>
    public string? GetActorBranch(ClaimsPrincipal user)
    {
        if (user.FindFirst("sub") is null)
            return null;

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