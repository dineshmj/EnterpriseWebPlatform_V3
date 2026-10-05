namespace EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;

/// <summary>Base of every rule the Accounts domain refuses to break.</summary>
public abstract class AccountsDomainException(string message) : Exception(message);

/// <summary>The command is invalid in itself (e.g. rejecting without remarks). Maps to 400.</summary>
public sealed class DomainRuleViolationException(string message) : AccountsDomainException(message);

/// <summary>The command is valid but the application is not in a state that allows it. Maps to 409.</summary>
public sealed class DomainConflictException(string message) : AccountsDomainException(message);

/// <summary>Separation of Duties forbids this actor from this application. Maps to 403.</summary>
public sealed class SeparationOfDutiesViolationException(string message) : AccountsDomainException(message);

/// <summary>ReBAC: the application is assigned to a different officer. Maps to 403.</summary>
public sealed class AssignmentViolationException(string message) : AccountsDomainException(message);