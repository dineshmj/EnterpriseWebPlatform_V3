using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.Customers;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ The IDP is the source of truth for scope names (ConfigRegistration/MicroserviceApiScopes),
// │ API resource names / audiences (ConfigRegistration/ApiResources) and client IDs
// │ (appsettings.json ClientIds). No shared library carries them any more: each component
// │ repeats the names it uses (scope checks, audiences, its own client ID), so a rename here
// │ must be made there too - search the repository for the old name.
// └──────────────────────────────────────────────────────────────────────────────────────
public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
        [
            new IdentityResources.OpenId(),
            new IdentityResources.Profile(),
            new IdentityResources.Email(),
            new (name: "roles", displayName: "User Roles", userClaims: [ "role" ]),

            // Organizational (ABAC) attributes of an employee. BFFs request this
            // scope so they know the acting user's branch (e.g. to stop early when a
            // user has none). Documents Management reads it from its own token.
            new (name: "organization", displayName: "Organization", userClaims:
                [ "employee_id", "lan_id", "department", "branch", "branch_city", "branch_country_code", "region", "clearance_level", "employment_type" ]),

            // A customer of the bank (My EWP): their customer number. Never on a staff token.
            new (name: "customer", displayName: "Customer", userClaims: [ "customer_number" ])
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
            PaymentsApiScope.Write,

            // Notifications API
            NotificationsApiScope.Read,
            NotificationsApiScope.Write,

            // Audit (Domain API and Journey API)
            AuditApiScope.Read,
            AuditApiScope.JourneyRead,

            // Operations (Journey API only)
            OperationsApiScope.JourneyRead,
            OperationsApiScope.JourneyWrite,

            // Partner Products (Domain API and Journey API)
            PartnerProductsApiScope.Read,
            PartnerProductsApiScope.Write,
            PartnerProductsApiScope.JourneyRead,
            PartnerProductsApiScope.JourneyWrite,

            // The customers' channel (My EWP) and the customer's own data in the domain APIs
            SelfServiceApiScope.JourneyRead,
            SelfServiceApiScope.AccountsCustomerRead,
            SelfServiceApiScope.PartnerProductsCustomerRead,
            SelfServiceApiScope.CustomerOnboardingOnlineIdentity
        ];

    public static IEnumerable<ApiResource> ApiResources =>
        [
            CustomerOnboardingApiResource.ApiResource,
            CustomerKycApiResource.ApiResource,
            DocumentsManagementApiResource.ApiResource,
            ComplianceApiResource.ApiResource,
            AccountsApiResource.ApiResource,
            PaymentsApiResource.ApiResource,
            NotificationsApiResource.ApiResource,
            AuditApiResource.ApiResource,
            AuditApiResource.JourneyApiResource,
            OperationsApiResource.ApiResource,
            PartnerProductsApiResource.ApiResource,
            PartnerProductsApiResource.JourneyApiResource,
            SelfServiceApiResource.ApiResource
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
            MfeCompliance.Client,
            MfeAccounts.Client,
            MfePayments.Client,
            MfeAudit.Client,
            MfeOperations.Client,
            MfePartnerProducts.Client,

            // M2M Clients
            KycCaseOpeningSubscriberToCustomerKycApiM2M.Client,
            OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M.Client,
            ComplianceCaseOpeningSubscriberToComplianceApiM2M.Client,
            DocumentInvalidationSubscriberToDocumentsManagementApiM2M.Client,
            AccountApplicationOpeningSubscriberToAccountsApiM2M.Client,
            NotificationsSubscriberToNotificationsApiM2M.Client,
            AccountsCommandSubscriberToAccountsApiM2M.Client,
            PaymentsSagaReplySubscriberToPaymentsApiM2M.Client,
            PartnerProductsWorkerM2M.Client,

            // Token exchange only (delegation): the Journey APIs act for a signed-in person.
            AuditJourneyApiTokenExchange.Client,
            OperationsJourneyApiTokenExchange.Client,
            PartnerProductsJourneyApiTokenExchange.Client,

            // The customers' channel (FAPI 2.0: PAR, private_key_jwt, DPoP) - customers only.
            SelfServiceWeb.Client,
            SelfServiceJourneyApiTokenExchange.Client
        ];
}