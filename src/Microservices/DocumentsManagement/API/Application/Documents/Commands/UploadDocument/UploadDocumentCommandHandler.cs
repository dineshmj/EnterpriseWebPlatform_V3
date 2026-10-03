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

        // Verify the real content type from the file signature before anything
        // is stored. A non-seekable stream is buffered so it can be rewound.
        var content = command.Content;
        if (!content.CanSeek)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;
            content = buffered;
        }

        var header = new byte[DocumentContentPolicy.SignatureLength];
        var headerLength = await content.ReadAtLeastAsync(
            header,
            header.Length,
            throwOnEndOfStream: false,
            cancellationToken);
        content.Position = 0;

        var verifiedContentType = DocumentContentPolicy.VerifyContentType(
            header.AsSpan(0, headerLength),
            command.ContentType);

        var stored = await storage.StoreAsync(
            documentId,
            fileName,
            content,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var document = Document.Create(
            documentId,
            fileName,
            verifiedContentType,
            stored.Size,
            stored.ContentHash,
            stored.StorageReference,
            now,
            command.ResourceBranch,
            command.DocumentType,
            command.BusinessReference);

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