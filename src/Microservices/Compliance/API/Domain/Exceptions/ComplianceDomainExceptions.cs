namespace EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;

/// <summary>Base of every rule the Compliance domain refuses to break.</summary>
public abstract class ComplianceDomainException(string message) : Exception(message);

/// <summary>The command is invalid in itself (e.g. rejecting without remarks). Maps to 400.</summary>
public sealed class DomainRuleViolationException(string message) : ComplianceDomainException(message);

/// <summary>The command is valid but the case is not in a state that allows it. Maps to 409.</summary>
public sealed class DomainConflictException(string message) : ComplianceDomainException(message);

/// <summary>Separation of Duties forbids this actor from this case. Maps to 403.</summary>
public sealed class SeparationOfDutiesViolationException(string message) : ComplianceDomainException(message);

/// <summary>ReBAC: the case is assigned to a different officer. Maps to 403.</summary>
public sealed class AssignmentViolationException(string message) : ComplianceDomainException(message);

/// <summary>ABAC: the officer's clearance is below what the case's risk requires. Maps to 403.</summary>
public sealed class ClearanceViolationException(string message) : ComplianceDomainException(message);