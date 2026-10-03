namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

/// <summary>
/// Consistency boundary: the only object repositories load and save, and the
/// only place domain events are raised. The unit of work writes the collected
/// events to the Outbox in the same transaction as the aggregate's state.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}