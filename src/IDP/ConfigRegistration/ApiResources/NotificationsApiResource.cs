using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class NotificationsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.NOTIFICATIONS_API,
                "Notifications API")
        {
            Scopes =
                {
                    NotificationsApiScopesRequired.NOTIFICATIONS_READ,
                    NotificationsApiScopesRequired.NOTIFICATIONS_WRITE
                },

            // The person's audiences are derived from these: their own subject ID, plus each
            // work-queue role in their branch (new-work notifications for the team).
            UserClaims =
                {
                    "role",
                    "branch",
                    "lan_id"
                }
        };
}