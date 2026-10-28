using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;
using EnterpriseWebPlatform.DocumentsManagement.API.Authorization;
using EnterpriseWebPlatform.DocumentsManagement.API.Bian;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: /v2 = BIAN DocumentDirectory, next to /v1 ──────────────────────
// │ Why : Shows the Documents Management API speaking BIAN's standard contract (release 14.0.0) to
// │       anyone who expects it, while EWP's own callers keep /v1.
// │ How : Two read-only BIAN operations, each on the SAME query, policy and object-level check as
// │       GET /v1/documents/{id}:
// │         GET v2/DocumentDirectory/{id}/Retrieve                                       (the entry)
// │         GET v2/DocumentDirectory/{id}/DocumentReferenceProperties/primary/Retrieve  (version, type)
// │       NOT offered: BIAN's Register and Update. Their body (the directory entry) holds an id,
// │       a date and a status - no file - while EWP stores nothing it has not checked (signature,
// │       hash, encryption); and a document's state changes only through the saga's internal
// │       steps (attached, invalidated), never by a caller's edit. The content stays on /v1.
// │ Ref : doc/bian/README.md | BIAN DocumentDirectory.yaml (release 14.0.0) | OWASP API1, API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("v2/DocumentDirectory")]
[FeatureGate(FeatureFlagKeys.BianV2Endpoints, defaultValue: true)]   // release toggle: off = 404 (Common/WebUtilities/FeatureFlags)
public sealed class DocumentDirectoryController(
    GetDocumentQueryHandler getDocumentHandler,
    IDocumentRepository repository,
    DocumentResourceAuthorization resourceAuthorization) : ControllerBase
{
    /// <summary>BIAN Retrieve: the directory entry. A document of another branch does not exist for the caller (as in /v1).</summary>
    [HttpGet("{documentdirectoryid:guid}/Retrieve")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<IActionResult> Retrieve(Guid documentdirectoryid, CancellationToken cancellationToken)
    {
        var document = await VisibleDocumentAsync(documentdirectoryid, cancellationToken);
        return document is null ? NotFound() : Bian(DocumentDirectoryMapper.ToEntry(document));
    }

    /// <summary>BIAN RetrieveDocumentReferenceProperties: the document's version and type.</summary>
    [HttpGet("{documentdirectoryid:guid}/DocumentReferenceProperties/{documentreferencepropertiesid}/Retrieve")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<IActionResult> RetrieveDocumentReferenceProperties(
        Guid documentdirectoryid,
        string documentreferencepropertiesid,
        CancellationToken cancellationToken)
    {
        if (documentreferencepropertiesid != DocumentDirectoryMapper.ReferencePropertiesId)
            return NotFound();

        var document = await VisibleDocumentAsync(documentdirectoryid, cancellationToken);
        return document is null ? NotFound() : Bian(DocumentDirectoryMapper.ToReferenceProperties(document));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────
    // │ /v1's object-level check: the caller's branch from their exchanged token must own the
    // │ document; otherwise 404 rather than 403, so no caller can probe which documents exist.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    private async Task<DocumentDetailsDto?> VisibleDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(documentId, cancellationToken);
        return document is not null && resourceAuthorization.CanAccess(document, User)
            ? await getDocumentHandler.HandleAsync(documentId, cancellationToken)
            : null;
    }

    private static JsonResult Bian(object body) => new(body, BianJson.Options);
}