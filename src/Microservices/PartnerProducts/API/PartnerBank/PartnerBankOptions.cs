namespace EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

/// <summary>The "PartnerBank" section: how EWP reaches the Partner Bank as its distributor.</summary>
public sealed class PartnerBankOptions
{
    public const string SectionName = "PartnerBank";

    /// <summary>The Partner Bank's token endpoint on its mutual-TLS port, e.g. https://partner-auth.ewp.home.arpa:47411/connect/token.</summary>
    public string TokenEndpoint { get; init; } = string.Empty;

    /// <summary>The Partner Bank's API for distributors (mutual TLS), e.g. https://partner-api.ewp.home.arpa:47403.</summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>EWP's client ID at the Partner Bank's authorization server.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>EWP's mutual-TLS client certificate (PKCS#12) and the file holding its password.</summary>
    public string ClientCertificatePath { get; init; } = string.Empty;

    public string ClientCertificatePasswordPath { get; init; } = string.Empty;

    /// <summary>EWP's request-signing key (ECDSA P-256, PKCS#12) and its password file: every instruction is signed with it.</summary>
    public string SigningCertificatePath { get; init; } = string.Empty;

    public string SigningCertificatePasswordPath { get; init; } = string.Empty;

    /// <summary>The ONLY root the Partner Bank's server certificate may chain to.</summary>
    public string TrustRootPath { get; init; } = string.Empty;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Addresses and key FILE PATHS only - never a key or password in configuration: the
    // │ keys stay in %USERPROFILE%\.ewp-pki (a key vault in production). Outside Development
    // │ the two URLs must use these host names (ExternalEndpoint: no SSRF by configuration).
    // └──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Outside Development: the host names the two URLs may use (see ExternalEndpoint).</summary>
    public string[] PermittedHosts { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 15;

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Where the purchases' money goes: the Partner Bank's settlement account, from
    // │ configuration only (checked at start-up) - the counter never types a payee, so a
    // │ purchase can never pay anyone but the Partner Bank.
    // └──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The Partner Bank's settlement account for Digital Gold purchases.</summary>
    public SettlementOptions Settlement { get; init; } = new();

    public sealed class SettlementOptions
    {
        public string PayeeName { get; init; } = string.Empty;

        public string Bsb { get; init; } = string.Empty;

        public string AccountNumber { get; init; } = string.Empty;
    }
}