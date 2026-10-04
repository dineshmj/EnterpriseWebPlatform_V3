namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class ComplianceMicroservice
{
    // Compliance Case Opening Subscriber to Compliance API M2M
    public const string CLIENT_NAME_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M = "Compliance Case Opening Subscriber to Compliance API M2M Client";

    public const string CLIENT_ID_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M = "Compliance.CaseOpeningSubscriber.To.ComplianceApi.M2M.ClientID";

    /// <summary>Reserved for the Compliance BFF + MFE (increment 2b); already registered in the Shell menu.</summary>
    public const string BFF_CLIENT_BASE_URL = "https://compliance.dev.localhost:44399";

    public const string MICROSERVICE_API_BASE_URL = "https://compliance-api.dev.localhost:44306";
}