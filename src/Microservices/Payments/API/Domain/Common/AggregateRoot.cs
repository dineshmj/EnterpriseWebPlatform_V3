namespace EnterpriseWebPlatform.Payments.Api.Domain.Common;

/// <summary>A fact that happened inside the Payments domain, raised by an aggregate.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Base class for aggregate roots: the only objects repositories load and save, and the
/// only place domain events are raised. Events (and the saga's commands) are written to
/// the Outbox by the unit of work in the same transaction as the change.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}