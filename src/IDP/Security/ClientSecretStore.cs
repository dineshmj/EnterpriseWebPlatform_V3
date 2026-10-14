using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY :: Client secrets from configuration only ────────────────────────────────────────
// │ Why : A client secret compiled into code is shared by everyone who can read the repository.
// │ How : Each secret comes from this deployable's configuration (Development settings or a secret
// │       store); a missing secret stops the start-up.
// │ Ref : Features 2.4.5, 1.6.5 | OWASP A05 / A07 | CWE-798 (hard-coded credentials) |
// │       Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If a client has no secret configured, stop the start-up - never run with a default or
        // │ empty secret.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"No client secret is configured for '{clientId}' (configuration key 'ClientSecrets:{clientId}').");
        }

        return new Secret(secret.Sha256());
    }
}