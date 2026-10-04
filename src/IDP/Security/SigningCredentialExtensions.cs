using System.Security.Cryptography.X509Certificates;

namespace EnterpriseWebPlatform.IdentityServer.Security;

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