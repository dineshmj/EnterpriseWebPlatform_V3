using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;

public sealed class Document : Entity
{
    private Document()
    {
    }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public string ContentHash { get; private set; } = string.Empty;

    public string StorageReference { get; private set; } = string.Empty;

    public string? DocumentType { get; private set; }

    public string? BusinessReference { get; private set; }

    /// <summary>
    /// Organizational (branch) scope of the document, taken from the actor who
    /// uploaded it. Used for object-level authorization. A document without a
    /// resource branch is accessible to nobody (fail closed).
    /// </summary>
    public string? ResourceBranch { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Document Create(
        Guid documentId,
        string fileName,
        string contentType,
        long size,
        string contentHash,
        string storageReference,
        DateTimeOffset now,
        string resourceBranch,
        string? documentType = null,
        string? businessReference = null)
    {
        if (string.IsNullOrWhiteSpace(resourceBranch))
            throw new DomainRuleViolationException("A resource branch is required.");

        if (documentId == Guid.Empty)
            throw new DomainRuleViolationException("Document ID cannot be empty.");

        if (string.IsNullOrWhiteSpace(fileName))
            throw new DomainRuleViolationException("File name is required.");

        if (string.IsNullOrWhiteSpace(contentType))
            throw new DomainRuleViolationException("Content type is required.");

        if (size < 0)
            throw new DomainRuleViolationException("Document size cannot be negative.");

        if (string.IsNullOrWhiteSpace(contentHash))
            throw new DomainRuleViolationException("Content hash is required.");

        if (string.IsNullOrWhiteSpace(storageReference))
            throw new DomainRuleViolationException("Storage reference is required.");

        return new Document
        {
            Id = documentId,
            FileName = fileName,
            ContentType = contentType,
            Size = size,
            ContentHash = contentHash,
            StorageReference = storageReference,
            DocumentType = string.IsNullOrWhiteSpace(documentType) ? null : documentType.Trim(),
            BusinessReference = string.IsNullOrWhiteSpace(businessReference) ? null : businessReference.Trim(),
            ResourceBranch = resourceBranch.Trim().ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}