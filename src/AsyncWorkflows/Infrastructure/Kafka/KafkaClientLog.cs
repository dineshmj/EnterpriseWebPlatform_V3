using Microsoft.Extensions.Logging;

using Confluent.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

// ┌─ :: ARCHITECTURE :: Kafka client messages through the application's logging ──────────────────
// │ Why : Left alone, the Kafka library (librdkafka) prints straight to the console: its lines
// │       never reach Serilog (no level, no trace, no central log store), and while the broker is
// │       down every client prints two lines every 2-3 seconds - the real messages drown.
// │ How : Every client built here sets this log and error handler. The library's own chatter
// │       goes to Debug (its warnings, e.g. configuration, stay Warning). Each kind of error
// │       (code) is a Warning the first time, then again only after 5 minutes while it lasts;
// │       repeats in between are Debug. A fatal error is always Critical. Outbox and subscriber code still log
// │       their own outcome (failed attempt, parked, published) as before.
// │ Ref : Customer observability stack (Serilog) | librdkafka log and error callbacks
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class KafkaClientLog(ILogger logger, string client)
{
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    // Per error code: while the broker is down the library alternates "connect failed" and "all
    // brokers are down" - each is said once, then again only after RepeatAfter.
    private readonly Dictionary<ErrorCode, DateTimeOffset> _lastWarnedAt = [];

    /// <summary>A line from the library itself: Debug, except its own warnings.</summary>
    public void OnLog(LogMessage message) =>
        logger.Log(message.Level == SyslogLevel.Warning ? LogLevel.Warning : LogLevel.Debug,
            "Kafka {Client} {Facility}: {Message}", client, message.Facility, message.Message);

    /// <summary>An error event (broker unreachable, authentication refused, ...), said once per change.</summary>
    public void OnError(Error error)
    {
        if (error.IsFatal)
        {
            logger.LogCritical("Kafka {Client} fatal error {Code}: {Reason}", client, error.Code, error.Reason);
            return;
        }

        bool warn;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            warn = !_lastWarnedAt.TryGetValue(error.Code, out var warnedAt) || now - warnedAt >= RepeatAfter;
            if (warn)
                _lastWarnedAt[error.Code] = now;
        }

        logger.Log(warn ? LogLevel.Warning : LogLevel.Debug,
            "Kafka {Client} error {Code}: {Reason}", client, error.Code, error.Reason);
    }
}

public static class KafkaClientLogExtensions
{
    /// <summary>Sends the producer's library messages and errors through <paramref name="log"/> instead of the console.</summary>
    public static ProducerBuilder<TKey, TValue> WithClientLog<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder, KafkaClientLog log) =>
        builder.SetLogHandler((_, message) => log.OnLog(message)).SetErrorHandler((_, error) => log.OnError(error));
}