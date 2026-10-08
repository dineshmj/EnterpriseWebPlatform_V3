using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// W3C trace-context propagation across the Outbox and Kafka, so that one trace
/// spans every hop of a workflow:
///
///   request (Activity) ──► Outbox row keeps its traceparent ──► relay "publish" span
///   ──► Kafka header "traceparent" ──► subscriber "process" span ──► HttpClient ──► next API
///
/// Uses System.Diagnostics only: the spans are exported when the host has
/// OpenTelemetry tracing configured (<see cref="ObservabilityExtensions"/>), and
/// cost nothing otherwise. Span names and tags follow the OpenTelemetry messaging
/// semantic conventions.
/// </summary>
public static class MessagingTelemetry
{
    public const string SourceName = "EnterpriseWebPlatform.Messaging";

    public const string TraceParentHeader = "traceparent";
    public const string MessageIdHeader = "message-id";
    public const string EventTypeHeader = "event-type";
    public const string WorkflowIdHeader = "workflow-id";
    public const string CorrelationIdHeader = "correlation-id";
    public const string CausationIdHeader = "causation-id";

    private static readonly ActivitySource Source = new(SourceName);

    // Metrics share the name: ewp_messaging_consumed_total{topic,outcome}. The publish side
    // (ewp_messaging_published_total) is counted by the Kafka producer.
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>(
        "ewp.messaging.consumed", description: "Kafka messages handled by a subscriber, by outcome.");

    /// <summary>Outcome of one consumed message: "processed", "dead_lettered" or "retried" (transient failure).</summary>
    public static void RecordConsumed(string topic, string outcome) =>
        Consumed.Add(1, new KeyValuePair<string, object?>("topic", topic), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// The current trace context in W3C traceparent format, to be stored with an
    /// Outbox row written during this request; null outside a trace.
    /// </summary>
    public static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;

    /// <summary>
    /// Starts the producer span for publishing an Outbox message, as a child of the
    /// trace that raised the event (<paramref name="storedTraceParent"/>).
    /// </summary>
    public static Activity? StartPublish(string topic, Guid messageId, string eventType, string? storedTraceParent)
    {
        ActivityContext.TryParse(storedTraceParent, null, out var parent);

        var activity = Source.StartActivity($"{topic} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("ewp.event_type", eventType);
        return activity;
    }

    /// <summary>
    /// Starts a span for background work that continues an earlier request's trace
    /// (<paramref name="storedTraceParent"/>, saved when the request asked for the work) -
    /// e.g. a worker opening an account long after the officer approved it. Events the
    /// work raises then record this trace, so the workflow stays one trace end to end.
    /// </summary>
    public static Activity? StartContinuation(string name, string? storedTraceParent)
    {
        ActivityContext.TryParse(storedTraceParent, null, out var parent);
        return Source.StartActivity(name, ActivityKind.Internal, parent);
    }

    /// <summary>
    /// Kafka headers for a published message: the trace context (the publish span,
    /// or the stored context when no span is recorded) and the workflow identity,
    /// so infrastructure can route and inspect messages without parsing bodies.
    /// </summary>
    public static Dictionary<string, string> PublishHeaders(
        Activity? publishActivity,
        string? storedTraceParent,
        Guid messageId,
        string eventType,
        Guid? workflowId,
        Guid? correlationId,
        Guid? causationId)
    {
        var headers = new Dictionary<string, string>
        {
            [MessageIdHeader] = messageId.ToString(),
            [EventTypeHeader] = eventType
        };

        var traceParent = publishActivity?.Id ?? storedTraceParent;
        if (!string.IsNullOrEmpty(traceParent))
            headers[TraceParentHeader] = traceParent;
        if (workflowId is { } w)
            headers[WorkflowIdHeader] = w.ToString();
        if (correlationId is { } c)
            headers[CorrelationIdHeader] = c.ToString();
        if (causationId is { } ca)
            headers[CausationIdHeader] = ca.ToString();

        return headers;
    }

    /// <summary>
    /// Starts the consumer span for processing a consumed message, as a child of
    /// the producer's span (from the "traceparent" header). Calls made while it is
    /// current (HttpClient) carry the trace on to the next service.
    /// </summary>
    public static Activity? StartProcess(string topic, int partition, long offset, IReadOnlyDictionary<string, string> headers)
    {
        headers.TryGetValue(TraceParentHeader, out var traceParent);
        ActivityContext.TryParse(traceParent, null, out var parent);

        var activity = Source.StartActivity($"{topic} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("messaging.destination.partition.id", partition.ToString());
        activity?.SetTag("messaging.kafka.offset", offset);
        if (headers.TryGetValue(MessageIdHeader, out var messageId))
            activity?.SetTag("messaging.message.id", messageId);
        if (headers.TryGetValue(WorkflowIdHeader, out var workflowId))
            activity?.SetTag("ewp.workflow_id", workflowId);
        return activity;
    }
}