using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocuments;

public sealed class GetDocumentsQueryHandler(IDocumentRepository repository)
{
    public async Task<IReadOnlyList<DocumentListItemDto>> HandleAsync(
        CancellationToken cancellationToken)
    {
        var documents = await repository.GetAllAsync(cancellationToken);

        return documents
            .Select(document => new DocumentListItemDto(
                document.Id,
                document.FileName,
                document.ContentType,
                document.Size,
                document.ContentHash,
                document.CreatedAt,
                document.Version))
            .ToList();
    }
}

public sealed record DocumentListItemDto(
    Guid DocumentId,
    string FileName,
    string ContentType,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    long Version);
