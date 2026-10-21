using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class PaymentsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "payments.read",
                "Payments API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                "payments.write",
                "Payments API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}