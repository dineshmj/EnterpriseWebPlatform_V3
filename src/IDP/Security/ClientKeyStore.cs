using System.Text.Json.Nodes;

using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY :: private_key_jwt: the IDP holds only public keys ───────────────────────────────
// │ Why : A shared client secret is known to both sides and travels with every token request;
// │       FAPI 2.0 asks for client authentication by an asymmetric key instead (or mutual TLS).
// │ How : The client signs a short assertion with its PRIVATE key (which never leaves it); the IDP
// │       registers only the PUBLIC key, read at start-up from the file named in
// │       "ClientKeys:<client id>" (a JWK made by ps/pki/New-EwpLocalPki.ps1). A missing file, a
// │       key that is not EC P-256 for ES256, or a file that also holds the private part stops the
// │       start-up.
// │ Ref : Features 2.1.6 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523 | OIDC Core §9 |
// │       Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The public JSON Web Keys of the clients that authenticate with private_key_jwt.</summary>
public static class ClientKeyStore
{
    private static IConfiguration? _configuration;

    public static void Initialize(IConfiguration configuration) =>
        _configuration = configuration;

    public static Secret For(string clientId)
    {
        var configuration = _configuration
            ?? throw new InvalidOperationException("ClientKeyStore has not been initialized.");

        var configured = configuration[$"ClientKeys:{clientId}"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"No public key file is configured for '{clientId}' (configuration key 'ClientKeys:{clientId}').");

        var path = Environment.ExpandEnvironmentVariables(configured);
        if (!File.Exists(path))
            throw new InvalidOperationException($"The public key file of '{clientId}' does not exist: {path}. Run ps/pki/New-EwpLocalPki.ps1.");

        var jwk = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"The public key file of '{clientId}' is not a JSON Web Key.");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the file also holds the private part ("d"), refuse: the IDP must never be able
        // │ to sign as its client. Only EC P-256 (ES256), marked for signatures, with a key ID.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (jwk.ContainsKey("d"))
            throw new InvalidOperationException($"The key file of '{clientId}' holds a PRIVATE key; the IDP takes the public key only.");
        if ((string?)jwk["kty"] != "EC" || (string?)jwk["crv"] != "P-256" || (string?)jwk["alg"] is not (null or "ES256"))
            throw new InvalidOperationException($"The key of '{clientId}' must be an EC P-256 key for ES256.");
        if (string.IsNullOrWhiteSpace((string?)jwk["kid"]))
            throw new InvalidOperationException($"The key of '{clientId}' has no key ID (kid).");

        return new Secret(jwk.ToJsonString(), $"{clientId} (private_key_jwt)")
        {
            Type = IdentityServerConstants.SecretTypes.JsonWebKey
        };
    }
}