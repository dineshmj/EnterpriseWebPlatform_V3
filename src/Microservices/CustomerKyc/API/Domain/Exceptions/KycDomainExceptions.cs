namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

/// <summary>Base of every rule the KYC domain refuses to break.</summary>
public abstract class KycDomainException(string message) : Exception(message);

/// <summary>The command is invalid in itself (e.g. rejecting without remarks). Maps to 400.</summary>
public sealed class DomainRuleViolationException(string message) : KycDomainException(message);

/// <summary>The command is valid but the case is no longer in a state that allows it. Maps to 409.</summary>
public sealed class DomainConflictException(string message) : KycDomainException(message);

/// <summary>Separation of Duties forbids this actor from this decision. Maps to 403.</summary>
public sealed class SeparationOfDutiesViolationException(string message) : KycDomainException(message);

/// <summary>ReBAC: the case is assigned to a different officer. Maps to 403.</summary>
public sealed class AssignmentViolationException(string message) : KycDomainException(message);
