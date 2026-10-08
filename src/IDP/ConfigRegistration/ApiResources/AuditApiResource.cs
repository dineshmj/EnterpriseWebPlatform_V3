using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Audit context's two APIs. Both receive DELEGATED tokens only (token exchange): the person
/// (sub, with their role, permissions and LAN ID) plus the acting client chain ("act").
/// </summary>
public sealed class AuditApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.AUDIT_API,
                "Audit API")
        {
            Scopes = { AuditApiScopesRequired.AUDIT_READ },

            // Who is reading the trail (auditor permissions), and the LAN ID recorded on the
            // ACCESS entry each read leaves behind.
            UserClaims = { "role", "permission", "lan_id", "department", "branch" }
        };

    public static ApiResource JourneyApiResource =>
        new(
                MicroserviceApiResourceNames.AUDIT_JOURNEY_API,
                "Audit Journey API")
        {
            Scopes = { AuditApiScopesRequired.AUDIT_JOURNEY_READ },
            UserClaims = { "role", "permission", "lan_id" }
        };
}