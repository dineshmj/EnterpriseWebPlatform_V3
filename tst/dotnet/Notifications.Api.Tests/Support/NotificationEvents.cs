using System.Text.Json;

namespace EnterpriseWebPlatform.Notifications.Api.Tests.Support;

/// <summary>
/// A workflow event as the NotificationsSubscriber hands it over: the shared envelope with
/// the initiator (subject + LAN ID) and the event's own payload.
/// </summary>
internal static class NotificationEvents
{
    public const string RequesterUserId = "8F1D7A52-0D0E-4C43-9A8E-0B6D2D0E1A01";

    public static JsonElement Envelope(string eventType, object payload, string? initiatorUserId = RequesterUserId, string? initiatorLanId = "dacoo") =>
        JsonSerializer.SerializeToElement(new
        {
            MessageId = Guid.NewGuid(),
            EventType = eventType,
            SchemaVersion = 1,
            Source = "customer-onboarding",
            OccurredAt = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            InitiatedByUserId = initiatorUserId,
            InitiatedByLanId = initiatorLanId,
            Payload = payload
        });

    public static JsonElement TargetOf(string? target) => JsonDocument.Parse(target!).RootElement.Clone();
}