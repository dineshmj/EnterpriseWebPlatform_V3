namespace EnterpriseWebPlatform.Compliance.Bff.Web.Configuration;

public sealed class ComplianceBffOptions
{
    public const string SectionName = "ComplianceBff";

    /// <summary>From configuration only (https, checked at start-up); no compiled-in default.</summary>
    public string ComplianceApiBaseUrl { get; init; } = string.Empty;
}