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
            new (name: "roles", displayName: "User Roles", userClaims: [ "role" ])
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
            AccountsApiResource.ApiResource,
            PaymentsApiResource.ApiResource
        ];

    public static IEnumerable<Client> Clients =>
        [
            // Customer Onboarding API - Bruno OAuth 2.0 client
            Bruno.Client,

            // Customer Onboarding API - BSS OAuth 2.0 Shell Application client
            BssClient.Client,

            // Mfes Clients
            MfeCustomerOnboarding.Client,
            MfeCustomerKyc.Client,
            MfeDocumentsManagement.Client,
            MfeAccounts.Client,
            MfePayments.Client,

            // M2M Clients
            CustomerOnboardingBFFToDocumentsManagementM2M.Client,
            CustomerKycSubscriberToCustomerKycApiM2M.Client
		];
}