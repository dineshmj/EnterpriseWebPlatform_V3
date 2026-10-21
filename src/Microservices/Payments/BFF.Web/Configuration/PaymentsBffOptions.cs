namespace EnterpriseWebPlatform.Payments.Bff.Web.Configuration;

public sealed class PaymentsBffOptions
{
    public const string SectionName = "PaymentsBff";

    public const string PaymentsApiClient = "PaymentsApi";

    public const string AccountsApiClient = "AccountsApi";

    /// <summary>From configuration only (https, checked at start-up); no compiled-in default.</summary>
    public string PaymentsApiBaseUrl { get; init; } = string.Empty;

    /// <summary>Read-only use: the customer's paying accounts and what is available.</summary>
    /// <summary>From configuration only (https, checked at start-up); no compiled-in default.</summary>
    public string AccountsApiBaseUrl { get; init; } = string.Empty;
}