namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;

public sealed class CustomerOutboxPublisherOptions
{
    public const string SectionName = "CustomerOutboxPublisher";

    public int BatchSize { get; init; } = 50;

    public int PollIntervalSeconds { get; init; } = 10;

    /// <summary>
    /// After this many failed attempts a message is parked (dead-lettered in
    /// place): it stays unpublished, is no longer retried, and - to preserve
    /// ordering - holds back later messages of the same aggregate until an
    /// operator resolves it (e.g. resets attempt_count after fixing the cause).
    /// </summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>Base of the exponential retry backoff: base * 2^(attempts - 1).</summary>
    public int RetryBaseDelaySeconds { get; init; } = 5;
}
