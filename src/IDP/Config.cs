using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
        [
            new IdentityResources.OpenId(),
            new IdentityResources.Profile(),
            new IdentityResources.Email(),
            new (name: "roles", displayName: "User Roles", userClaims: [ "role" ]),

            // Organizational (ABAC) attributes of an employee. BFFs request this
            // scope so they know the acting user's branch, e.g. to pass it to
            // Documents Management for branch-scoped document access.
            new (name: "organization", displayName: "Organization", userClaims:
                [ "employee_id", "department", "branch", "branch_city", "branch_country_code", "region", "clearance_level", "employment_type" ])
        ];

    public static IEnumerable<ApiScope> ApiScopes =>
        [
            // Customer Onboarding API
            CustomerOnboardingApiScope.Read,
            CustomerOnboardingApiScope.Write,
            
            // Customer KYC API
            CustomerKycApiScope.Read,
            CustomerKycApiScope.Write,

            // Documents Management API
            DocumentsManagementApiScope.Read,
            DocumentsManagementApiScope.Write,

            // Compliance API
            ComplianceApiScope.Read,
            ComplianceApiScope.Write,

            // Accounts API
            AccountsApiScope.Read,
            AccountsApiScope.Write,

            // Payments API
            PaymentsApiScope.Read,
            PaymentsApiScope.Write            
        ];

    public static IEnumerable<ApiResource> ApiResources =>
        [
            CustomerOnboardingApiResource.ApiResource,
            CustomerKycApiResource.ApiResource,
            DocumentsManagementApiResource.ApiResource,
            ComplianceApiResource.ApiResource,
            AccountsApiResource.ApiResource,
            PaymentsApiResource.ApiResource
        ];

    /// <summary>
    /// Client registrations. The Bruno API-testing client is a public client and
    /// is registered only in Development.
    /// </summary>
    public static IEnumerable<Client> GetClients(bool isDevelopment) =>
        isDevelopment ? [Bruno.Client, .. Clients] : Clients;

    private static IEnumerable<Client> Clients =>
        [
            // Customer Onboarding API - BSS OAuth 2.0 Shell Application client
            BssClient.Client,

            // Mfes Clients
            MfeCustomerOnboarding.Client,
            MfeCustomerKyc.Client,
            MfeAccounts.Client,
            MfePayments.Client,

            // M2M Clients
            CustomerOnboardingBFFToDocumentsManagementM2M.Client,
            KycCaseOpeningSubscriberToCustomerKycApiM2M.Client,
            OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M.Client,
            ComplianceCaseOpeningSubscriberToComplianceApiM2M.Client,
            KycBFFToDocumentsManagementM2M.Client
        ];
}