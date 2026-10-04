using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Events;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;

/// <summary>
/// Aggregate root: one stored, verified document.
///
/// Invariants:
///  - The content type is one DM detected from the file signature (see
///    <see cref="Policies.DocumentContentPolicy"/>), never the caller's claim.
///  - Content is identified by its SHA-256 hash and an opaque storage reference.
///  - Every new document belongs to a branch; a document without a resource
///    branch (legacy data only) is accessible to nobody (fail closed).
///  - A document is immutable once stored; it can only be removed.
/// </summary>
public sealed class Document : AggregateRoot
{
    // For EF Core materialization.
    private Document()
    {
        FileName = null!;
        ContentHash = null!;
    }

    public FileName FileName { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public ContentHash ContentHash { get; private set; }

    public string StorageReference { get; private set; } = string.Empty;

    public string? DocumentType { get; private set; }

    public string? BusinessReference { get; private set; }

    /// <summary>
    /// Organizational (branch) scope of the document, taken from the actor who
    /// uploaded it. Used for object-level authorization.
    /// </summary>
    public BranchCode? ResourceBranch { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Document Upload(
        Guid documentId,
        FileName fileName,
        string verifiedContentType,
        long size,
        ContentHash contentHash,
        string storageReference,
        BranchCode resourceBranch,
        DateTimeOffset now,
        string? documentType = null,
        string? businessReference = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(resourceBranch);

        if (documentId == Guid.Empty)
            throw new DomainRuleViolationException("Document ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(verifiedContentType))
            throw new DomainRuleViolationException("Content type is required.");

        if (size <= 0)
            throw new DomainRuleViolationException("A document cannot be empty.");

        if (string.IsNullOrWhiteSpace(storageReference))
            throw new DomainRuleViolationException("Storage reference is required.");

        var document = new Document
        {
            Id = documentId,
            FileName = fileName,
            ContentType = verifiedContentType,
            Size = size,
            ContentHash = contentHash,
            StorageReference = storageReference,
            DocumentType = string.IsNullOrWhiteSpace(documentType) ? null : documentType.Trim(),
            BusinessReference = string.IsNullOrWhiteSpace(businessReference) ? null : businessReference.Trim(),
            ResourceBranch = resourceBranch,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        document.RaiseDomainEvent(new DocumentUploadedDomainEvent(
            documentId,
            resourceBranch.Value,
            document.DocumentType,
            document.BusinessReference,
            verifiedContentType,
            now));

        return document;
    }

    public bool BelongsTo(BranchCode? branch) =>
        branch is not null && ResourceBranch is not null && ResourceBranch == branch;
}