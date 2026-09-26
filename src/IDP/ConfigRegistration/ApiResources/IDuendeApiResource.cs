using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public interface IDuendeApiResource
{
    public abstract static ApiResource ApiResource { get; }
}