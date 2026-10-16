namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>Base of every rule the Partner Products domain refuses to break.</summary>
public abstract class DigitalGoldException(string message) : Exception(message);

/// <summary>The form or slip is invalid in itself (e.g. a form signed in the future). Maps to 400.</summary>
public sealed class DigitalGoldRuleViolationException(string message) : DigitalGoldException(message);

/// <summary>Valid in itself, but not now (e.g. a second Form VII, or a slip before the account is open). Maps to 409.</summary>
public sealed class DigitalGoldConflictException(string message) : DigitalGoldException(message);

/// <summary>The Partner Bank gave no answer (unreachable, timed out): nothing final happened - send again. Maps to 503.</summary>
public sealed class PartnerBankUnavailableException(string message) : DigitalGoldException(message);