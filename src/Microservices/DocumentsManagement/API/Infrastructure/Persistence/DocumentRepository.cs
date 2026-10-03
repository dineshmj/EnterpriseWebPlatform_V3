using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

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

    public async Task<IReadOnlyList<Document>> ListAsync(
        BranchCode resourceBranch,
        string? businessReference,
        string? documentType,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Documents
            .AsNoTracking()
            .Where(x => x.ResourceBranch == resourceBranch);

        if (!string.IsNullOrWhiteSpace(businessReference))
        {
            var reference = businessReference.Trim();
            query = query.Where(x => x.BusinessReference == reference);
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            var type = documentType.Trim();
            query = query.Where(x => x.DocumentType == type);
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public void Remove(Document document) => dbContext.Documents.Remove(document);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}