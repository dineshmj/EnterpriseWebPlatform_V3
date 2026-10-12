using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Operations Journey API. It receives DELEGATED tokens only (token exchange by the Operations
/// web BFF): the person (role, permissions, LAN ID, and how and when they signed in - for the
/// step-up on simulator changes) plus the acting client ("act").
/// </summary>
public sealed class OperationsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.OPERATIONS_JOURNEY_API,
                "Operations Journey API")
        {
            Scopes = { OperationsApiScopesRequired.OPERATIONS_JOURNEY_READ, OperationsApiScopesRequired.OPERATIONS_JOURNEY_WRITE },
            UserClaims = { "role", "permission", "lan_id" }
        };
}