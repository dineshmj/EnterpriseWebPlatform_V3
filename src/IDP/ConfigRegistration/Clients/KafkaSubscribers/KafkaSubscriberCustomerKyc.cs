using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.KafkaSubscribers.IdpInfo;
using EnterpriseWebPlatform.Common.Landscape.Microservices;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.KafkaSubscribers;

public sealed class KafkaSubscriberCustomerKyc
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Accounts Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
                new()
                {
                    ClientId = CustomerKycKafkaSubscriber.CLIENT_ID_FOR_IDP,
                    ClientName = CustomerKycKafkaSubscriber.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { new Secret(CustomerKycKafkaSubscriber.CLIENT_SECRET_FOR_IDP.Sha256()) },

                    AllowedGrantTypes = GrantTypes.ClientCredentials,
                    // 🡡__ WHY   : The Customer KYC Kafka Subscriber is a machine-to-machine (M2M) client and does not represent an interactive
                    //              user session. The Client Credentials grant allows the subscriber to authenticate itself using its client
                    //              credentials and obtain an access token for calling the protected Customer KYC Microservice API.
                    // 🡡__ IF NOT: Using an interactive authorization flow such as Authorization Code would introduce browser-based user
                    //              authentication and redirect handling that are not applicable to a background Kafka subscriber. It would
                    //              also incorrectly model the subscriber as a user-facing application rather than an independent machine identity.

                    AllowedScopes =
                    {
                        MicroserviceApiResourceNames.CUSTOMER_KYC_API
                            // 🡡__ WHY   : Including the CUSTOMER_KYC_API scope allows the Customer KYC Kafka Subscriber to request an access token
                            //              for calling the protected Customer KYC Microservice API. The API validates the token and its granted
                            //              scope before allowing the subscriber to invoke the protected endpoint.
                            // 🡡__ IF NOT: If this scope is not allowed for the client, the IDP will not grant the requested API permission in
                            //              the access token. The subscriber will therefore be unable to obtain an access token suitable for
                            //              calling the protected Customer KYC Microservice API.
                    }
                };
        }
    }
}