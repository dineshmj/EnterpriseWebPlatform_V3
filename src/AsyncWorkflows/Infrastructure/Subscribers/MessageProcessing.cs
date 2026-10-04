namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

/// <summary>One message as consumed from Kafka.</summary>
public sealed record ConsumedMessage(
    string Topic,
    int Partition,
    long Offset,
    string? Key,
    string Value,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>The decision for one consumed message.</summary>
public sealed record ProcessingOutcome(bool DeadLetter, string? Reason)
{
    /// <summary>Done (applied, already applied, or nothing to do): commit the offset.</summary>
    public static ProcessingOutcome Processed { get; } = new(false, null);

    /// <summary>The message can never succeed: park it on the dead-letter topic, then commit.</summary>
    public static ProcessingOutcome ToDeadLetter(string reason) => new(true, reason);
}

/// <summary>
/// A dependency (IDP, the context's API, Kafka) is unavailable or failing; the
/// message itself is fine. It is retried in place - never skipped or dead-lettered.
/// </summary>
public sealed class TransientProcessingException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The worker-specific part of a subscriber: turns one message into a call to the
/// owning context's API and classifies the result. Returns <see cref="ProcessingOutcome"/>
/// for processed / dead-letter, and throws <see cref="TransientProcessingException"/>
/// (or any other exception) for "retry later".
/// </summary>
public interface IMessageProcessor
{
    Task<ProcessingOutcome> ProcessAsync(ConsumedMessage message, CancellationToken cancellationToken);
}

/// <summary>Consumer settings every subscriber provides (bound from its own configuration section).</summary>
public interface ISubscriberSettings
{
    IReadOnlyList<string> Topics { get; }

    /// <summary>All instances share this group: Kafka gives each partition to exactly one instance.</summary>
    string GroupId { get; }

    string DeadLetterTopic { get; }

    /// <summary>Back-off while a transient failure persists (doubling, capped).</summary>
    int TransientRetryInitialDelaySeconds { get; }

    int TransientRetryMaxDelaySeconds { get; }
}

/// <summary>The subscriber's own machine identity (OAuth 2.0 Client Credentials).</summary>
public interface IM2MClientSettings
{
    string IdentityServerAuthority { get; }

    string ClientId { get; }

    /// <summary>From configuration / secret store only; never compiled in.</summary>
    string ClientSecret { get; }

    string Scope { get; }
}
