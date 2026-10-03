using System.Security.Claims;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Authorization;

/// <summary>
/// Object-level (branch-scoped) authorization for documents.
///
/// A human token carries its own "branch" claim. An M2M token has no human,
/// so only the BFF clients that are registered to call DM may state the
/// acting user's branch, through the X-Actor-Branch header. Any other M2M
/// client gets no actor branch and is therefore denied.
///
/// NOTE: the header is asserted by a trusted, pinned client. The target design
/// replaces it with delegated user context (token exchange / signed actor claim).
/// </summary>
public sealed class DocumentResourceAuthorization
{
    public const string ActorBranchHeader = "X-Actor-Branch";

    private static readonly HashSet<string> BranchAssertingClients = new(StringComparer.Ordinal)
    {
        DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_ONBOARDING_BFF_TO_DOC_MGMT_M2M,
        DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M
    };

    // Only the Customer Onboarding BFF removes documents (cleanup of an
    // unfinished submission). No other caller may delete.
    private static readonly HashSet<string> DeletingClients = new(StringComparer.Ordinal)
    {
        DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_ONBOARDING_BFF_TO_DOC_MGMT_M2M
    };

    public string? GetActorBranch(ClaimsPrincipal user, HttpRequest request)
    {
        var clientId = user.FindFirst("client_id")?.Value;
        var isM2m = user.FindFirst("sub") is null;

        string? branch;

        if (isM2m)
        {
            if (clientId is null || !BranchAssertingClients.Contains(clientId))
                return null;

            branch = request.Headers[ActorBranchHeader].FirstOrDefault();
        }
        else
        {
            branch = user.FindFirst("branch")?.Value;
        }

        return string.IsNullOrWhiteSpace(branch)
            ? null
            : branch.Trim().ToUpperInvariant();
    }

    public bool CanAccess(Document document, ClaimsPrincipal user, HttpRequest request) =>
        BranchCode.TryCreate(GetActorBranch(user, request), out var actorBranch) &&
        document.BelongsTo(actorBranch);

    public bool CanDelete(Document document, ClaimsPrincipal user, HttpRequest request)
    {
        var clientId = user.FindFirst("client_id")?.Value;

        return clientId is not null &&
            DeletingClients.Contains(clientId) &&
            CanAccess(document, user, request);
    }
}
