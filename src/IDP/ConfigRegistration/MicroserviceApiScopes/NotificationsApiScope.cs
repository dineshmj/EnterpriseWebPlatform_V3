using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class NotificationsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                NotificationsApiScopesRequired.NOTIFICATIONS_READ,
                "Notifications API - Read")
        {
            UserClaims = { "role", "branch" }
        };

    public static ApiScope Write =>
        new(
                NotificationsApiScopesRequired.NOTIFICATIONS_WRITE,
                "Notifications API - Write")
        {
            UserClaims = { "role", "branch" }
        };
}