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
///  - The content of a stored document never changes. Its lifecycle does: AVAILABLE,
///    then ATTACHED (submitted as evidence of an application) and/or INVALIDATED
///    (business compensation). Attached and invalidated documents are records kept for
///    audit / regulatory retention (AML/CTF): they can no longer be removed, and an
///    invalidated document never becomes attached again.
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

    public DocumentStatus Status { get; private set; } = DocumentStatus.Available;

    public DateTimeOffset? InvalidatedAt { get; private set; }

    /// <summary>Why the document was invalidated, e.g. which rejected application it belonged to.</summary>
    public string? InvalidationReason { get; private set; }

    public DateTimeOffset? AttachedAt { get; private set; }

    /// <summary>What the document is evidence of, e.g. the onboarding application number.</summary>
    public string? AttachedTo { get; private set; }

    /// <summary>
    /// Only an AVAILABLE document may be removed (cleanup of an upload that never became
    /// part of a submitted application). Attached and invalidated documents are retained.
    /// </summary>
    public bool CanBeRemoved => Status == DocumentStatus.Available;

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

    /// <summary>
    /// Records that the document was submitted as evidence (e.g. of an onboarding
    /// application): from now on it is retained. Idempotent: returns false when it is
    /// already attached, or already invalidated (a late fact never revives evidence).
    /// </summary>
    public bool Attach(string attachedTo, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(attachedTo))
            throw new DomainRuleViolationException("What the document is attached to is required.");

        if (Status != DocumentStatus.Available)
            return false;

        Status = DocumentStatus.Attached;
        AttachedAt = now;
        AttachedTo = attachedTo.Trim()[..Math.Min(attachedTo.Trim().Length, MaxAttachedToLength)];
        UpdatedAt = now;
        Version++;

        RaiseDomainEvent(new DocumentAttachedDomainEvent(Id, ResourceBranch?.Value ?? string.Empty, AttachedTo, now));
        return true;
    }

    public const int MaxAttachedToLength = 100;

    /// <summary>
    /// Marks the document as no longer valid evidence (business compensation, e.g. its
    /// onboarding application was rejected). The content is retained. Idempotent:
    /// returns false when the document is already invalidated.
    /// </summary>
    public bool Invalidate(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleViolationException("A reason is required to invalidate a document.");

        if (Status == DocumentStatus.Invalidated)
            return false;

        Status = DocumentStatus.Invalidated;
        InvalidatedAt = now;
        InvalidationReason = reason.Trim()[..Math.Min(reason.Trim().Length, MaxInvalidationReasonLength)];
        UpdatedAt = now;
        Version++;

        RaiseDomainEvent(new DocumentInvalidatedDomainEvent(Id, ResourceBranch?.Value ?? string.Empty, InvalidationReason, now));
        return true;
    }

    public const int MaxInvalidationReasonLength = 500;

    public bool BelongsTo(BranchCode? branch) =>
        branch is not null && ResourceBranch is not null && ResourceBranch == branch;
}