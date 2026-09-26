using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class PaymentsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.PAYMENTS_API,
                "Payments API")
        {
            Scopes =
                {
                    PaymentsApiScopesRequired.PAYMENTS_READ,
                    PaymentsApiScopesRequired.PAYMENTS_WRITE
                },

            UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
        };
}