namespace EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

public sealed class DomainRuleViolationException(string message) : Exception(message);
