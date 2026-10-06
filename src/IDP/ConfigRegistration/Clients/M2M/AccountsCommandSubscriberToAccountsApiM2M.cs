using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the AccountsCommandSubscriber: it may only hand funds commands (from
/// the Payments saga orchestrator) to the Accounts API (accounts.write), which pins this
/// client ID on its internal endpoint.
/// </summary>
public sealed class AccountsCommandSubscriberToAccountsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNTS_COMMAND_SUBSCRIBER_TO_ACCOUNTS_API_M2M,
            ClientName = AccountsMicroservice.CLIENT_NAME_FOR_IDP_FOR_ACCOUNTS_COMMAND_SUBSCRIBER_TO_ACCOUNTS_API_M2M,
            ClientSecrets = { ClientSecretStore.For(AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNTS_COMMAND_SUBSCRIBER_TO_ACCOUNTS_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { AccountsApiScopesRequired.ACCOUNTS_WRITE }
        };
}