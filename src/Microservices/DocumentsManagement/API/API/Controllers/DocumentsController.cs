using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    IDocumentStorage storage) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "DocumentRead")]
    public async Task<ActionResult<IReadOnlyList<DocumentListItemDto>>> GetDocuments(
        [FromQuery] string? businessReference,
        [FromQuery] string? documentType,
        CancellationToken cancellationToken)
    {
        return Ok(await getDocumentsHandler.HandleAsync(
            businessReference,
            documentType,
            cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<ActionResult<DocumentDetailsDto>> GetDocument(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await getDocumentHandler.HandleAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = "DocumentRead")]
    public async Task<IActionResult> GetContent(
        Guid id,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document is null)
            return NotFound();

        var stream = await storage.OpenReadAsync(
            document.StorageReference,
            cancellationToken);

        if (stream is null)
            return NotFound();

        return File(
            stream,
            document.ContentType,
            document.FileName,
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
        if (document is null)
            return NotFound();

        repository.Remove(document);
        await repository.SaveChangesAsync(cancellationToken);

        await storage.DeleteAsync(document.StorageReference, cancellationToken);

        return NoContent();
    }
}
