using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using EnterpriseWebPlatform.Audit.Api.Domain;

namespace EnterpriseWebPlatform.Audit.Api.Application;

/// <summary>
/// Turns one consumed integration event into an audit entry. Tolerant reader: it takes only
/// the identifiers, the people and the outcome it knows by name, from the standard envelope
/// and its Payload (or from the root of an older, flat message), and ignores everything else -
/// in particular names, addresses and contact details, which the trail must not hold.
/// Anything a producer adds later is ignored until audit decides it needs it.
/// </summary>
public static class AuditEventMapper
{
    // Who acted, in order of preference: the decider of a decision, the approver of an opening.
    private static readonly (string UserId, string LanId)[] ActorFields =
    [
        ("DecisionByUserId", "DecisionByLanId"),
        ("ApprovedByUserId", "ApprovedByLanId")
    ];

    public static AuditEntry? Map(string topic, int partition, long offset, string rawMessage, DateTimeOffset recordedAt)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;   // not JSON at all: the caller dead-letters it
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var payload = Get(root, "Payload") is { ValueKind: JsonValueKind.Object } p ? p : root;

        var entry = new AuditEntry
        {
            // A message without a MessageId still gets a stable one (same message, same ID),
            // so a redelivery is recognised as a duplicate.
            MessageId = Id(root, "MessageId") ?? DeterministicId(topic, partition, offset),
            EntryKind = AuditEntryKind.Event,
            EventType = Limit(Text(root, "EventType"), 100) ?? topic,
            Source = Limit(Text(root, "Source"), 100),
            Topic = topic,
            KafkaPartition = partition,
            KafkaOffset = offset,
            OccurredAt = AuditChain.ToStoredPrecision(When(root, "OccurredAt") ?? recordedAt),
            RecordedAt = AuditChain.ToStoredPrecision(recordedAt),
            WorkflowId = Id(root, "WorkflowId"),
            CorrelationId = Id(root, "CorrelationId"),
            CausationId = Id(root, "CausationId"),
            InitiatedByUserId = Limit(Text(root, "InitiatedByUserId"), 200),
            InitiatedByLanId = Limit(Text(root, "InitiatedByLanId"), 50),
            CustomerNumber = Limit(Text(payload, "CustomerNumber"), 50),
            BranchCode = Limit(Text(payload, "BranchCode"), 20),
            ReasonCode = Limit(Text(payload, "ReasonCode") ?? Text(payload, "RejectedBy"), 100),
            Amount = Number(payload, "Amount"),
            Currency = Limit(Text(payload, "Currency"), 3),
            PayloadSha256 = AuditChain.Sha256Hex(Encoding.UTF8.GetBytes(rawMessage))
        };

        (entry.RecordType, entry.RecordRef) = RecordOf(payload);
        entry.Status = Limit(StatusOf(payload), 100);

        foreach (var (userField, lanField) in ActorFields)
        {
            if (Text(payload, userField) is { } actor)
            {
                entry.ActorUserId = Limit(actor, 200);
                entry.ActorLanId = Limit(Text(payload, lanField), 50);
                break;
            }
        }

        return entry;
    }

    /// <summary>The record an auditor searches by: the payment, else the onboarding application, else the customer.</summary>
    private static (string? Type, string? Ref) RecordOf(JsonElement payload) =>
        Text(payload, "PaymentNumber") is { } payment ? ("PAYMENT", Limit(payment, 100))
        : Text(payload, "ApplicationNumber") is { } application ? ("APPLICATION", Limit(application, 100))
        : Text(payload, "CustomerNumber") is { } customer ? ("CUSTOMER", Limit(customer, 100))
        : (null, null);

    private static string? StatusOf(JsonElement payload)
    {
        if (Text(payload, "Stage") is { } stage && Text(payload, "NewStageStatus") is { } stageStatus)
            return $"{stage}: {stageStatus}";
        return Text(payload, "NewStatus") ?? Text(payload, "Status") ?? Text(payload, "HoldStatus");
    }

    private static Guid DeterministicId(string topic, int partition, long offset)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{topic}|{partition}|{offset}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static JsonElement? Get(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static string? Text(JsonElement element, string name) => Get(element, name) switch
    {
        { ValueKind: JsonValueKind.String } v when !string.IsNullOrWhiteSpace(v.GetString()) => v.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null
    };

    private static Guid? Id(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } v && v.TryGetGuid(out var id) && id != Guid.Empty ? id : null;

    private static DateTimeOffset? When(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } v && v.TryGetDateTimeOffset(out var time) ? time : null;

    private static decimal? Number(JsonElement element, string name) => Get(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetDecimal(out var d) => Math.Round(d, 2),
        { ValueKind: JsonValueKind.String } v when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => Math.Round(d, 2),
        _ => null
    };

    private static string? Limit(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}