namespace EnterpriseWebPlatform.Common.Observability;

/// <summary>
/// A friendly console window title ("Customer Onboarding API" instead of the EXE's full path), so
/// the 20+ console windows of a local run can be told apart.
/// </summary>
public static class ConsoleTitle
{
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api"] = "API", ["bff"] = "BFF", ["kyc"] = "KYC", ["idp"] = "IDP"
    };

    /// <summary>"customer-onboarding-api" → "Customer Onboarding API".</summary>
    public static string FromServiceName(string serviceName) =>
        string.Join(' ', serviceName
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Acronyms.TryGetValue(word, out var acronym) ? acronym : char.ToUpperInvariant(word[0]) + word[1..]));

    /// <summary>Sets the title of this process's console window, when it has one.</summary>
    public static void Set(string title)
    {
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Only on Windows with a real console: a service host, container or redirected output
        // │ has no window to name - and a title must never stop the service from starting.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
            return;
        try
        {
            Console.Title = title;
        }
        catch (IOException)
        {
            // No console attached (e.g. started by a service manager): nothing to name.
        }
    }
}