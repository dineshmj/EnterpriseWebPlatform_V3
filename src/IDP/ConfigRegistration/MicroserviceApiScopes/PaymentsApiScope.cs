using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class PaymentsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                PaymentsApiScopesRequired.PAYMENTS_READ,
                "Payments API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                PaymentsApiScopesRequired.PAYMENTS_WRITE,
                "Payments API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}