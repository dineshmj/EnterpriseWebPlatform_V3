namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

/// <summary>
/// Start-up check for the addresses a worker sends its bearer token to (the IDP and its API):
/// configuration only, never compiled in, and always an absolute https URL.
/// </summary>
public static class ServiceAddress
{
    public static bool IsHttps(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}