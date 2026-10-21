namespace EnterpriseWebPlatform.Common.WebUtilities.Configuration;

// ┌─ :: SECURITY :: Addresses and identifiers from configuration, fail closed ────────────────────
// │ Why : A service address or client ID that silently falls back to a compiled-in value (or to an
// │       empty one) sends tokens to the wrong place, or pins an authorization rule to nothing.
// │ How : A value that is missing or blank stops the start-up; a service address must be an
// │       absolute https URL (bearer tokens travel there). Trailing slashes are removed.
// │ Ref : Blueprint §7 (independent deployability) | OWASP A05 Security Misconfiguration |
// │       CWE-319 | ASVS V14.1
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Checks a value read from configuration and names its key when it is unusable.</summary>
public static class RequiredSetting
{
    /// <summary>The trimmed value; throws when it is missing or blank.</summary>
    public static string Text(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is not configured.")
            : value.Trim();

    /// <summary>The address without a trailing slash; throws unless it is an absolute https URL.</summary>
    public static string HttpsUrl(string? value, string key)
    {
        var text = Text(value, key).TrimEnd('/');
        return IsHttpsUrl(text)
            ? text
            : throw new InvalidOperationException($"{key} must be an absolute https address (is '{text}').");
    }

    /// <summary>True for an absolute https URL.</summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}