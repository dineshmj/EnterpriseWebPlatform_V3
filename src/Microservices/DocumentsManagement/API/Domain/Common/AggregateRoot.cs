namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Common;

/// <summary>A fact that happened inside the Documents Management domain.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Consistency boundary and the only place domain events are raised. Documents
/// Management has no Outbox yet (no context consumes its events), so the unit of
/// work clears the events after saving; they become integration events when a
/// consumer appears (e.g. an audit trail).
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
