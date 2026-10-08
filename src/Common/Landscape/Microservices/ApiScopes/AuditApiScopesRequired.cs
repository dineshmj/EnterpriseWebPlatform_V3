namespace EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

public static class AuditApiScopesRequired
{
    /// <summary>Read the audit trail (Audit Domain API) - only in a token exchanged by the Audit Journey API for an auditor.</summary>
    public const string AUDIT_READ =
        "audit.read";

    /// <summary>Call the Audit Journey API - only in a token exchanged by the Audit web BFF for the signed-in person.</summary>
    public const string AUDIT_JOURNEY_READ =
        "audit-journey.read";
}