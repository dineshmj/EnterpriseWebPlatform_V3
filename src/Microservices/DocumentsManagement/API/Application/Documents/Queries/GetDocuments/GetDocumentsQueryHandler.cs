using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocuments;

public sealed class GetDocumentsQueryHandler(IDocumentRepository repository)
{
    public const int MaxPageSize = 100;

    public async Task<IReadOnlyList<DocumentListItemDto>> HandleAsync(
        string resourceBranch,
        string? businessReference,
        string? documentType,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var documents = await repository.ListAsync(
            resourceBranch,
            businessReference,
            documentType,
            Math.Max(1, pageNumber),
            Math.Clamp(pageSize, 1, MaxPageSize),
            cancellationToken);

        return documents
            .Select(document => new DocumentListItemDto(
                document.Id,
                document.FileName,
                document.ContentType,
                document.Size,
                document.ContentHash,
                document.CreatedAt,
                document.Version,
                document.DocumentType,
                document.BusinessReference))
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
    long Version,
    string? DocumentType,
    string? BusinessReference);
