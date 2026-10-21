using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class NotificationsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "notifications-api",
                "Notifications API")
        {
            Scopes =
                {
                    "notifications.read",
                    "notifications.write"
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