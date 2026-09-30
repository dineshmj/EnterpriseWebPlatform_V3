using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;

public interface IDocumentRepository
{
    Task AddAsync(Document document, CancellationToken cancellationToken);

    Task<Document?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken);

    void Remove(Document document);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}