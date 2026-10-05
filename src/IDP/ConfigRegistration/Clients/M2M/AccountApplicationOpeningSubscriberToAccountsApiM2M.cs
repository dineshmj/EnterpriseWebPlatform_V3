using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the AccountApplicationOpeningSubscriber: it may only open
/// account applications (accounts.write), and the Accounts API pins this client
/// ID on its internal endpoint.
/// </summary>
public sealed class AccountApplicationOpeningSubscriberToAccountsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M,
            ClientName = AccountsMicroservice.CLIENT_NAME_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M,
            ClientSecrets = { ClientSecretStore.For(AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { AccountsApiScopesRequired.ACCOUNTS_WRITE }
        };
}