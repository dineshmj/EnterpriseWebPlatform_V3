using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Events;

/// <summary>A document was invalidated as business compensation (retained, not deleted).</summary>
public sealed record DocumentInvalidatedDomainEvent(
    Guid DocumentId,
    string ResourceBranch,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;