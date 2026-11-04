using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;

// ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
// │ A worker's two keys made for one test: the private keys go to MachineKeys (which owns
// │ them), the public parts stay here so a test can check each signature as the IDP or
// │ the API would - with the public key only.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>A worker's client key and DPoP key for one test, and the JWT reading the tests share.</summary>
public sealed class TestKeys
{
    public TestKeys()
    {
        var client = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ClientPublic = client.ExportParameters(includePrivateParameters: false);
        DPoPPublic = dpop.ExportParameters(includePrivateParameters: false);
        Machine = new MachineKeys(client, dpop, TimeProvider.System);
    }

    public MachineKeys Machine { get; }

    public ECParameters ClientPublic { get; }

    public ECParameters DPoPPublic { get; }

    /// <summary>The header and the claims of a compact JWS (decoded, not verified).</summary>
    public static (JsonElement Header, JsonElement Claims) Read(string jwt)
    {
        var parts = jwt.Split('.');
        return (Json(parts[0]), Json(parts[1]));
    }

    /// <summary>Whether the JWS is signed (ES256) by the private half of this public key.</summary>
    public static bool IsSignedBy(string jwt, ECParameters publicKey)
    {
        using var key = ECDsa.Create(publicKey);
        var last = jwt.LastIndexOf('.');
        return key.VerifyData(Encoding.ASCII.GetBytes(jwt[..last]), Base64Url.DecodeFromChars(jwt.AsSpan(last + 1)), HashAlgorithmName.SHA256);
    }

    /// <summary>The RFC 7638 thumbprint of a public key - what the IDP puts in cnf.jkt.</summary>
    public static string Thumbprint(ECParameters publicKey) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.EncodeToString(publicKey.Q.X)}\",\"y\":\"{Base64Url.EncodeToString(publicKey.Q.Y)}\"}}")));

    /// <summary>The ath an API expects: the SHA-256 of the access token, base64url.</summary>
    public static string HashOf(string accessToken) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

    /// <summary>A form body (application/x-www-form-urlencoded) as name -> value.</summary>
    public static IReadOnlyDictionary<string, string> Form(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Decode(p[0]), p => p.Length > 1 ? Decode(p[1]) : string.Empty);

    /// <summary>Writes an EC P-256 private key as encrypted PKCS#8 PEM with its password file, as New-EwpClientKeys.mjs does.</summary>
    public static KeyFileSettings WriteKeyFile(string folder, string name, ECCurve? curve = null)
    {
        using var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        const string password = "test-password";
        var pem = PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", key.ExportEncryptedPkcs8PrivateKey(
            password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        var path = Path.Combine(folder, $"{name}.key.pem");
        File.WriteAllText(path, pem);
        File.WriteAllText($"{path}.password", password);
        return new KeyFileSettings { Path = path, PasswordPath = $"{path}.password" };
    }

    private static string Decode(string part) =>
        Uri.UnescapeDataString(part.Replace('+', ' '));

    private static JsonElement Json(string part) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(part)).RootElement;
}

/// <summary>A subscriber's machine-identity settings, as a test sets them.</summary>
public sealed class TestSettings : IM2MClientSettings
{
    public string IdentityServerAuthority { get; init; } = "https://idp.test";

    public string ClientId { get; init; } = "Test.Subscriber.M2M.ClientID";

    public KeyFileSettings ClientKey { get; init; } = new();

    public KeyFileSettings DPoPKey { get; init; } = new();

    public string Scope { get; init; } = "test.write";
}