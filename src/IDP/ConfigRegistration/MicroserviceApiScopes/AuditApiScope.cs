using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>Read-only by design (no Write): the trail is written from Kafka only.</summary>
public static class AuditApiScope
{
    /// <summary>The Audit Domain API, read-only (it has no write endpoint: entries come from Kafka).</summary>
    public static ApiScope Read =>
        new(
                AuditApiScopesRequired.AUDIT_READ,
                "Audit API - Read")
        {
            UserClaims = { "role", "name" }
        };

    /// <summary>The Audit Journey API (the Next.js BFF calls it for the signed-in auditor).</summary>
    public static ApiScope JourneyRead =>
        new(
                AuditApiScopesRequired.AUDIT_JOURNEY_READ,
                "Audit Journey API - Read")
        {
            UserClaims = { "role", "name" }
        };
}