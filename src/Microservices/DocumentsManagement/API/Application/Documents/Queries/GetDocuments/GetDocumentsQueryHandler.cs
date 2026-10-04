using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

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
        // An unusable branch sees nothing (fail closed).
        if (!BranchCode.TryCreate(resourceBranch, out var branch))
            return [];

        var documents = await repository.ListAsync(
            branch!,
            businessReference,
            documentType,
            Math.Max(1, pageNumber),
            Math.Clamp(pageSize, 1, MaxPageSize),
            cancellationToken);

        return documents
            .Select(document => new DocumentListItemDto(
                document.Id,
                document.FileName.Value,
                document.ContentType,
                document.Size,
                document.ContentHash.Value,
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