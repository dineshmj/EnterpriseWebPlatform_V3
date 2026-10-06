namespace EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

/// <summary>Base of every rule the Payments domain refuses to break.</summary>
public abstract class PaymentsDomainException(string message) : Exception(message);

/// <summary>The command is invalid in itself (e.g. a malformed BSB). Maps to 400.</summary>
public sealed class DomainRuleViolationException(string message) : PaymentsDomainException(message);

/// <summary>The command is valid but the payment or saga is not in a state that allows it. Maps to 409.</summary>
public sealed class DomainConflictException(string message) : PaymentsDomainException(message);