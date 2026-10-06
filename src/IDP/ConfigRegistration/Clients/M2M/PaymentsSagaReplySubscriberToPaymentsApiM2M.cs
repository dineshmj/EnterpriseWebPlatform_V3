using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the PaymentsSagaReplySubscriber: it may only hand Accounts' replies
/// to the payment saga orchestrator (payments.write); the Payments API pins this client ID
/// on its internal endpoint.
/// </summary>
public sealed class PaymentsSagaReplySubscriberToPaymentsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = PaymentsMicroservice.CLIENT_ID_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M,
            ClientName = PaymentsMicroservice.CLIENT_NAME_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M,
            ClientSecrets = { ClientSecretStore.For(PaymentsMicroservice.CLIENT_ID_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { PaymentsApiScopesRequired.PAYMENTS_WRITE }
        };
}