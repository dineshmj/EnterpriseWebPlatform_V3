namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions;

/// <summary>
/// The caller's expected version does not match: another request changed the
/// aggregate first (optimistic concurrency). Mapped to 409 with a safe message.
/// </summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
