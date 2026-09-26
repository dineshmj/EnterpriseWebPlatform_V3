using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;

public sealed class DocumentRepository(DocumentsManagementDbContext dbContext)
    : IDocumentRepository
{
    public async Task AddAsync(Document document, CancellationToken cancellationToken) =>
        await dbContext.Documents.AddAsync(document, cancellationToken);

    public Task<Document?> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
        dbContext.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == documentId, cancellationToken);

    public async Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Documents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Remove(Document document) => dbContext.Documents.Remove(document);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
