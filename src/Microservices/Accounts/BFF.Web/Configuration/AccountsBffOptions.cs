using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;

public sealed class AccountsBffOptions
{
    public const string SectionName = "AccountsBff";

    public string AccountsApiBaseUrl { get; init; } = AccountsMicroservice.MICROSERVICE_API_BASE_URL;
}