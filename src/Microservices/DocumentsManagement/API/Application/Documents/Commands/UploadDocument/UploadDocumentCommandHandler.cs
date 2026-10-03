using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Policies;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.UploadDocument;

public sealed class UploadDocumentCommandHandler(
    IDocumentRepository repository,
    IDocumentStorage storage,
    TimeProvider clock)
{
    public async Task<UploadDocumentResult> HandleAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var documentId = Guid.NewGuid();

        // Validate everything the caller supplied before any content is stored.
        var fileName = FileName.Create(command.FileName);
        var resourceBranch = BranchCode.Create(command.ResourceBranch);

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
            fileName.Value,
            content,
            cancellationToken);

        Document document;
        try
        {
            document = Document.Upload(
                documentId,
                fileName,
                verifiedContentType,
                stored.Size,
                ContentHash.Create(stored.ContentHash),
                stored.StorageReference,
                resourceBranch,
                clock.GetUtcNow(),
                command.DocumentType,
                command.BusinessReference);

            await repository.AddAsync(document, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Never leave stored content without its record.
            await storage.DeleteAsync(stored.StorageReference, CancellationToken.None);
            throw;
        }

        return new UploadDocumentResult(
            document.Id,
            document.FileName.Value,
            document.ContentType,
            document.Size,
            document.ContentHash.Value,
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