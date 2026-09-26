using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;

public interface IDuendeClient
{
    public abstract static Client Client { get; }
}