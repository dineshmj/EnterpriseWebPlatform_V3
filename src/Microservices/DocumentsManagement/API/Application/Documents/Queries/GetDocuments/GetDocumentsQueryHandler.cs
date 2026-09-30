using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocuments;

public sealed class GetDocumentsQueryHandler(IDocumentRepository repository)
{
    public async Task<IReadOnlyList<DocumentListItemDto>> HandleAsync(
        string? businessReference,
        string? documentType,
        CancellationToken cancellationToken)
    {
        var documents = await repository.GetAllAsync(cancellationToken);

        var filtered = documents.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(businessReference))
        {
            filtered = filtered.Where(document =>
                string.Equals(
                    document.BusinessReference,
                    businessReference,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            filtered = filtered.Where(document =>
                string.Equals(
                    document.DocumentType,
                    documentType,
                    StringComparison.OrdinalIgnoreCase));
        }

        return filtered
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