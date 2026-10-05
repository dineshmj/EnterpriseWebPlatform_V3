using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Events;

/// <summary>A document was submitted as evidence and is now retained (no longer deletable).</summary>
public sealed record DocumentAttachedDomainEvent(
    Guid DocumentId,
    string ResourceBranch,
    string AttachedTo,
    DateTimeOffset OccurredAt) : IDomainEvent;