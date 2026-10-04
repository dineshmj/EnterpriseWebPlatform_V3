using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.Compliance.Bff.Web.Configuration;

public sealed class ComplianceBffOptions
{
    public const string SectionName = "ComplianceBff";

    public string ComplianceApiBaseUrl { get; init; } = ComplianceMicroservice.MICROSERVICE_API_BASE_URL;
}