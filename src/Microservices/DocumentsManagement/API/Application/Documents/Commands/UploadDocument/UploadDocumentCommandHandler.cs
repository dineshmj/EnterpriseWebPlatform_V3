using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.UploadDocument;

public sealed class UploadDocumentCommandHandler(
    IDocumentRepository repository,
    IDocumentStorage storage)
{
    public async Task<UploadDocumentResult> HandleAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var documentId = Guid.NewGuid();
        var fileName = Path.GetFileName(command.FileName);

        var stored = await storage.StoreAsync(
            documentId,
            fileName,
            command.Content,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var document = Document.Create(
            documentId,
            fileName,
            string.IsNullOrWhiteSpace(command.ContentType)
                ? "application/octet-stream"
                : command.ContentType,
            stored.Size,
            stored.ContentHash,
            stored.StorageReference,
            now);

        await repository.AddAsync(document, cancellationToken);

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(stored.StorageReference, CancellationToken.None);
            throw;
        }

        return new UploadDocumentResult(
            document.Id,
            document.FileName,
            document.ContentType,
            document.Size,
            document.ContentHash,
            document.CreatedAt,
            document.Version);
    }
}

public sealed record UploadDocumentResult(
    Guid DocumentId,
    string FileName,
    string ContentType,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    long Version);
