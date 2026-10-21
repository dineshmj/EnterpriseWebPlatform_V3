using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class NotificationsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "notifications.read",
                "Notifications API - Read")
        {
            UserClaims = { "role", "branch" }
        };

    public static ApiScope Write =>
        new(
                "notifications.write",
                "Notifications API - Write")
        {
            UserClaims = { "role", "branch" }
        };
}