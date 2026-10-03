using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;

public sealed class GetDocumentQueryHandler(IDocumentRepository repository)
{
    public async Task<DocumentDetailsDto?> HandleAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetAsync(documentId, cancellationToken);
        if (document is null)
            return null;

        return new DocumentDetailsDto(
            document.Id,
            document.FileName.Value,
            document.ContentType,
            document.Size,
            document.ContentHash.Value,
            document.CreatedAt,
            document.UpdatedAt,
            document.Version);
    }
}

public sealed record DocumentDetailsDto(
    Guid DocumentId,
    string FileName,
    string ContentType,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);