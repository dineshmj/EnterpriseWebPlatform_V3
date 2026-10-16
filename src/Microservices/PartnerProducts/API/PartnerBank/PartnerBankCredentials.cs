using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

/// <summary>EWP's keys for the Partner Bank, loaded once at start-up - a missing file stops the API (fail closed).</summary>
public sealed class PartnerBankCredentials
{
    public X509Certificate2 ClientCertificate { get; }

    public X509Certificate2 SigningCertificate { get; }

    public X509Certificate2 TrustRoot { get; }

    public PartnerBankCredentials(PartnerBankOptions options)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The private keys live in files OUTSIDE the repository (%USERPROFILE%\.ewp-pki, made by
        // │ ps\pki\New-EwpLocalPki.ps1), each with its own password file. The signing key is held
        // │ in memory only (EphemeralKeySet). The TLS client key cannot be: Windows' TLS (SChannel)
        // │ uses only keys in a key container, so it goes into a temporary one, not persisted
        // │ (removed again with the certificate). Production: a key vault / HSM, the signing key
        // │ never exported (Partner Bank Threat-Model R8).
        // └──────────────────────────────────────────────────────────────────────────────────────
        ClientCertificate = LoadPfx(options.ClientCertificatePath, options.ClientCertificatePasswordPath, "EWP's mutual-TLS client certificate", X509KeyStorageFlags.DefaultKeySet);
        SigningCertificate = LoadPfx(options.SigningCertificatePath, options.SigningCertificatePasswordPath, "EWP's request-signing key", X509KeyStorageFlags.EphemeralKeySet);
        TrustRoot = X509CertificateLoader.LoadCertificateFromFile(Existing(options.TrustRootPath, "the trust root"));

        using var signingKey = SigningCertificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("EWP's request-signing certificate must hold an ECDSA (P-256) private key.");
        if (signingKey.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("EWP's request-signing key must be on the P-256 curve (ES256).");
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The Partner Bank's server is believed only if its certificate chains to the PoC's
    // │ own root (not to anything in the Windows store) AND its name matches the URL.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public bool IsTrustedServer(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(TrustRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // PoC CA has no revocation (Partner Bank R1)
        return chain.Build(new X509Certificate2(certificate));
    }

    private static X509Certificate2 LoadPfx(string path, string passwordPath, string what, X509KeyStorageFlags flags) =>
        X509CertificateLoader.LoadPkcs12FromFile(
            Existing(path, what),
            File.ReadAllText(Existing(passwordPath, $"the password of {what}")).Trim(),
            flags);

    private static string Existing(string configured, string what)
    {
        var path = Environment.ExpandEnvironmentVariables(configured);
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"PartnerBank: {what} was not found at '{path}' - run EWP's ps\\pki\\New-EwpLocalPki.ps1.");
    }
}