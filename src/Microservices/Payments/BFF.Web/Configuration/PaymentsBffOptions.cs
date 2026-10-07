using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.Payments.Bff.Web.Configuration;

public sealed class PaymentsBffOptions
{
    public const string SectionName = "PaymentsBff";

    public const string PaymentsApiClient = "PaymentsApi";

    public const string AccountsApiClient = "AccountsApi";

    public string PaymentsApiBaseUrl { get; init; } = PaymentsMicroservice.MICROSERVICE_API_BASE_URL;

    /// <summary>Read-only use: the customer's paying accounts and what is available.</summary>
    public string AccountsApiBaseUrl { get; init; } = AccountsMicroservice.MICROSERVICE_API_BASE_URL;
}