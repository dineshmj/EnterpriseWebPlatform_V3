using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.Security;

/// <summary>
/// Client secrets come from the IDP's own configuration
/// ("ClientSecrets:&lt;client id&gt;"), i.e. appsettings.Development.json locally and
/// environment variables / a secret store elsewhere - never from constants
/// compiled into a shared library. A confidential client without a configured
/// secret stops the IDP from starting (fail closed).
/// </summary>
public static class ClientSecretStore
{
    private static IConfiguration? _configuration;

    public static void Initialize(IConfiguration configuration) =>
        _configuration = configuration;

    public static Secret For(string clientId)
    {
        var configuration = _configuration
            ?? throw new InvalidOperationException("ClientSecretStore has not been initialized.");

        var secret = configuration[$"ClientSecrets:{clientId}"];

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"No client secret is configured for '{clientId}' (configuration key 'ClientSecrets:{clientId}').");
        }

        return new Secret(secret.Sha256());
    }
}