using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.DocumentsManagement.API.Authorization;
using EnterpriseWebPlatform.DocumentsManagement.API.Models;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.UploadDocument;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocuments;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Controllers;

[ApiController]
[Route("v1/documents")]
public sealed class DocumentsController(
    UploadDocumentCommandHandler uploadDocumentHandler,
    GetDocumentQueryHandler getDocumentHandler,
    GetDocumentsQueryHandler getDocumentsHandler,
    IDocumentRepository repository,
    IDocumentStorage storage,
    DocumentResourceAuthorization resourceAuthorization) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "DocumentRead")]
    public async Task<ActionResult<IReadOnlyList<DocumentListItemDto>>> GetDocuments(
        [FromQuery] string? businessReference,
        [FromQuery] string? documentType,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        // Listing is always confined to the actor's own branch.
        var actorBranch = resourceAuthorization.GetActorBranch(User, Request);
        if (actorBranch is null)
            return Forbid();

        return Ok(await getDocumentsHandler.HandleAsync(
            actorBranch,
            businessReference,
            documentType,
            pageNumber,
            pageSize,
            cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<ActionResult<DocumentDetailsDto>> GetDocument(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(id, cancellationToken);

        // 404 rather than 403, so a caller cannot probe which documents exist.
        if (document is null || !resourceAuthorization.CanAccess(document, User, Request))
            return NotFound();

        return Ok(await getDocumentHandler.HandleAsync(id, cancellationToken));
    }

    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<IActionResult> GetContent(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document is null || !resourceAuthorization.CanAccess(document, User, Request))
            return NotFound();

        var stream = await storage.OpenReadAsync(
            document.StorageReference,
            cancellationToken);

        if (stream is null)
            return NotFound();

        // Always an attachment with a DM-verified type, never sniffed by a browser.
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(
            stream,
            document.ContentType,
            document.FileName.Value,
            lastModified: document.UpdatedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    [HttpPost]
    [Authorize(Policy = "DocumentWrite")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<ActionResult<UploadDocumentResult>> Upload(
        [FromForm] UploadDocumentRequest request,
        [FromHeader(Name = "X-Document-Type")] string? documentType,
        [FromHeader(Name = "X-Business-Reference")] string? businessReference,
        CancellationToken cancellationToken)
    {
        var actorBranch = resourceAuthorization.GetActorBranch(User, Request);
        if (actorBranch is null)
            return Forbid();

        if (request.File is null || request.File.Length == 0)
        {
            ModelState.AddModelError(nameof(request.File), "A non-empty file is required.");
            return ValidationProblem(ModelState);
        }

        await using var stream = request.File.OpenReadStream();

        var result = await uploadDocumentHandler.HandleAsync(
            new UploadDocumentCommand(
                request.File.FileName,
                request.File.ContentType,
                stream,
                actorBranch,
                documentType,
                businessReference),
            cancellationToken);

        return CreatedAtAction(
            nameof(GetDocument),
            new { id = result.DocumentId },
            result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DocumentWrite")]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document is null || !resourceAuthorization.CanAccess(document, User, Request))
            return NotFound();

        if (!resourceAuthorization.CanDelete(document, User, Request))
            return Forbid();

        repository.Remove(document);
        await repository.SaveChangesAsync(cancellationToken);

        await storage.DeleteAsync(document.StorageReference, cancellationToken);

        return NoContent();
    }
}