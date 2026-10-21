using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Audit context's two APIs. Both receive DELEGATED tokens only (token exchange): the person
/// (sub, with their role, permissions and LAN ID) plus the acting client chain ("act").
/// </summary>
public sealed class AuditApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "audit-api",
                "Audit API")
        {
            Scopes = { "audit.read" },

            // Who is reading the trail (auditor permissions), and the LAN ID recorded on the
            // ACCESS entry each read leaves behind.
            UserClaims = { "role", "permission", "lan_id", "department", "branch" }
        };

    public static ApiResource JourneyApiResource =>
        new(
                "audit-journey-api",
                "Audit Journey API")
        {
            Scopes = { "audit-journey.read" },
            UserClaims = { "role", "permission", "lan_id" }
        };
}