using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public interface IDuendeMicroserviceApiScope
{
    public abstract static ApiScope Read { get; }

    public abstract static ApiScope Write { get; }
}