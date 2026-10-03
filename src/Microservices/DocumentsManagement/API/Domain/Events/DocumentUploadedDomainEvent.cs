using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Events;

/// <summary>A verified document was stored for a branch (not published yet - see AggregateRoot).</summary>
public sealed record DocumentUploadedDomainEvent(
    Guid DocumentId,
    string ResourceBranch,
    string? DocumentType,
    string? BusinessReference,
    string ContentType,
    DateTimeOffset OccurredAt) : IDomainEvent;
