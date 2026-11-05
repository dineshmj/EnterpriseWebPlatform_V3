namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

/// <summary>The action is valid, but not in the current state (e.g. a request already decided).</summary>
public sealed class DomainConflictException : Exception
{
    public DomainConflictException(string message) : base(message) { }
}