namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

/// <summary>Four eyes: the person who asked for something may not also decide it.</summary>
public sealed class SeparationOfDutiesViolationException : Exception
{
    public SeparationOfDutiesViolationException(string message) : base(message) { }
}