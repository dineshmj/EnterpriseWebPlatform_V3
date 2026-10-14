using System.Security.Cryptography.X509Certificates;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY :: Token-signing key ─────────────────────────────────────────────────────────────
// │ Why : Whoever holds the signing key can mint any token for any API.
// │ How : The developer signing key is used only in Development; elsewhere a certificate must be
// │       configured, or the IDP refuses to start.
// │ Ref : Features 1.6.5 | OWASP A02 Cryptographic Failures | ASVS V6.4 | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class SigningCredentialExtensions
{
    /// <summary>
    /// Development: an automatically generated developer key (tempkey.jwk).
    /// Any other environment: a certificate supplied through configuration /
    /// a secret store. The IDP refuses to start without one; it never falls back
    /// to a developer key outside Development.
    /// </summary>
    public static IIdentityServerBuilder AddSigningCredential(
        this IIdentityServerBuilder identityServer,
        WebApplicationBuilder builder)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If this is Development, use the developer signing key; anywhere else a signing
        // │ certificate is required, or the IDP does not start.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (builder.Environment.IsDevelopment())
        {
            return identityServer.AddDeveloperSigningCredential();
        }

        var certificatePath = builder.Configuration["SigningCredential:CertificatePath"];
        var certificatePassword = builder.Configuration["SigningCredential:CertificatePassword"];

        if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
        {
            throw new InvalidOperationException(
                "SigningCredential:CertificatePath must point to the token-signing certificate " +
                "outside the Development environment.");
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            certificatePassword);

        return identityServer.AddSigningCredential(certificate);
    }
}