namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

/// <summary>An object with identity. Only aggregate roots raise domain events.</summary>
public abstract class Entity
{
    public long Id { get; protected set; }
}
