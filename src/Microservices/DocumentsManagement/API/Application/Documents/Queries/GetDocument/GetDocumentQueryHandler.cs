using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

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
            document.Version,
            document.Status.ToCode(),
            document.InvalidatedAt,
            document.InvalidationReason,
            document.AttachedAt,
            document.AttachedTo);
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
    long Version,
    string Status,
    DateTimeOffset? InvalidatedAt,
    string? InvalidationReason,
    DateTimeOffset? AttachedAt,
    string? AttachedTo);