namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>
/// What happened to a Form VII or a purchase, raised by the aggregate itself at each change of
/// status and written to the Outbox in the same transaction (PartnerProductsDbContext).
/// </summary>
public sealed record DigitalGoldEvent(string EventType, DateTimeOffset OccurredAt, string? Reason = null, bool Resend = false);

/// <summary>An aggregate that raises Digital Gold events (collected until the next save).</summary>
public interface IRaisesDigitalGoldEvents
{
    IReadOnlyList<DigitalGoldEvent> Events { get; }

    void ClearEvents();
}

/// <summary>The published event types (topic partner-products.digital-gold.events, Integration-Event-Catalogue.md).</summary>
public static class DigitalGoldEventTypes
{
    public const string ApplicationRecorded = "GoldAccountApplicationRecorded";
    public const string ApplicationSent = "GoldAccountApplicationSent";
    public const string AccountOpened = "GoldAccountOpened";
    public const string AccountLinked = "GoldAccountLinked";
    public const string ApplicationRefused = "GoldAccountApplicationRefused";

    public const string PurchaseRecorded = "GoldPurchaseRecorded";
    public const string PurchaseSent = "GoldPurchaseSent";
    public const string PurchaseOrdered = "GoldPurchaseOrdered";
    public const string PurchaseRefused = "GoldPurchaseRefused";
    public const string PurchasePaymentStarted = "GoldPurchasePaymentStarted";
    public const string PurchasePaid = "GoldPurchasePaid";
    public const string PurchaseAllocated = "GoldPurchaseAllocated";
    public const string PurchaseCancelled = "GoldPurchaseCancelled";
    public const string PurchaseNeedsAttention = "GoldPurchaseNeedsAttention";
}