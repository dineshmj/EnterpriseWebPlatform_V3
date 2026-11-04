using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: SECURITY :: private_key_jwt and DPoP: the worker's two keys ───────────────────────────────
// │ Why : A shared client secret is known to the IDP too and travels with every token request; a
// │       bearer token works for whoever holds it - copied from a log, a memory dump or a proxy, it
// │       is as good as the worker's own. FAPI 2.0 asks for an asymmetric client key and
// │       sender-constrained tokens, for machines as for people.
// │ How : Two EC P-256 private keys (encrypted PKCS#8 PEM, each with a password file), read once at
// │       start-up; a missing or unreadable file, another curve, or the SAME key twice stops the
// │       worker. The client key signs a 60 s client assertion per token request (iss = sub = the
// │       client ID, aud = the IDP's issuer only, typ client-authentication+jwt, a new jti, kid = the
// │       RFC 7638 thumbprint New-EwpClientKeys.mjs gave the public key). The DPoP key signs a new
// │       proof for every request (htm, htu without the query, iat, jti, and ath on an API call);
// │       its public part travels in the proof, the IDP binds each token to it (cnf.jkt).
// │ Ref : Features 2.1.2 | FAPI 2.0 Security Profile §5.3.1, §5.3.2.2 | RFC 7523, 7638, 9449 |
// │       Threat-Model R-9, R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The worker's client key (private_key_jwt) and DPoP key, and the JWTs signed with them.</summary>
public sealed class MachineKeys : IDisposable
{
    private readonly ECDsa _clientKey;

    private readonly ECDsa _dpopKey;

    private readonly string _clientKeyId;

    private readonly object _dpopPublicJwk;

    private readonly TimeProvider _time;

    public MachineKeys(ECDsa clientKey, ECDsa dpopKey, TimeProvider time)
    {
        _clientKey = clientKey;
        _dpopKey = dpopKey;
        _time = time;
        _clientKeyId = Thumbprint(clientKey);
        DPoPThumbprint = Thumbprint(dpopKey);

        // SEC: "I am this client" and "I hold this token" stay apart - one key for both is refused.
        if (_clientKeyId == DPoPThumbprint)
            throw new InvalidOperationException("The DPoP key must not be the client key.");

        var point = dpopKey.ExportParameters(includePrivateParameters: false).Q;
        _dpopPublicJwk = new { kty = "EC", crv = "P-256", x = Base64Url.EncodeToString(point.X), y = Base64Url.EncodeToString(point.Y) };
    }

    /// <summary>The RFC 7638 thumbprint of the DPoP key: the cnf.jkt of every token bound to it.</summary>
    public string DPoPThumbprint { get; }

    /// <summary>Reads both keys from the files the settings name (fail closed).</summary>
    public static MachineKeys Load(IM2MClientSettings settings, TimeProvider time) =>
        new(Read(settings.ClientKey, "ClientKey"), Read(settings.DPoPKey, "DPoPKey"), time);

    /// <summary>A one-time client assertion for ONE token request (RFC 7523, private_key_jwt).</summary>
    public string ClientAssertion(string clientId, string issuer)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        return Sign(_clientKey,
            new { alg = "ES256", typ = "client-authentication+jwt", kid = _clientKeyId },
            new { iss = clientId, sub = clientId, aud = issuer, iat = now, nbf = now, exp = now + 60, jti = Guid.NewGuid().ToString("N") });
    }

    /// <summary>
    /// A DPoP proof for exactly ONE request (RFC 9449): its method and address without the query,
    /// a new jti, now - plus the hash of the token it goes with on an API call (ath), and the
    /// server's nonce when it asked for one.
    /// </summary>
    public string Proof(HttpMethod method, Uri address, string? accessToken = null, string? nonce = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["htm"] = method.Method,
            ["htu"] = address.GetLeftPart(UriPartial.Path),
            ["iat"] = _time.GetUtcNow().ToUnixTimeSeconds()
        };
        if (accessToken is not null)
            claims["ath"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
        if (nonce is not null)
            claims["nonce"] = nonce;

        return Sign(_dpopKey, new { typ = "dpop+jwt", alg = "ES256", jwk = _dpopPublicJwk }, claims);
    }

    public void Dispose()
    {
        _clientKey.Dispose();
        _dpopKey.Dispose();
    }

    private static ECDsa Read(KeyFileSettings file, string name)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromEncryptedPem(
                File.ReadAllText(Environment.ExpandEnvironmentVariables(file.Path)),
                File.ReadAllText(Environment.ExpandEnvironmentVariables(file.PasswordPath)).Trim());
            if (ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new InvalidOperationException($"The {name} must be an EC P-256 key (ES256).");
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    // RFC 7638: SHA-256 over the required members, in lexical order, without spaces.
    private static string Thumbprint(ECDsa key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.EncodeToString(point.X)}\",\"y\":\"{Base64Url.EncodeToString(point.Y)}\"}}";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // A compact JWS signed with ES256 (.NET signs in the IEEE P1363 form JWS asks for).
    private static string Sign(ECDsa key, object header, object payload)
    {
        var unsigned = $"{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        return $"{unsigned}.{Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256))}";
    }
}