using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

public interface IDocumentRepository
{
    Task AddAsync(Document document, CancellationToken cancellationToken);

    Task<Document?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists documents within one resource branch. Filtering and paging are
    /// executed by the database; callers can never list across branches.
    /// </summary>
    Task<IReadOnlyList<Document>> ListAsync(
        string resourceBranch,
        string? businessReference,
        string? documentType,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken);

    void Remove(Document document);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
