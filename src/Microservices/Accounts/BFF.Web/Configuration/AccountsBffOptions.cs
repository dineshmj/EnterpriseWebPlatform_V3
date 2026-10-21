namespace EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;

public sealed class AccountsBffOptions
{
    public const string SectionName = "AccountsBff";

    /// <summary>From configuration only (https, checked at start-up); no compiled-in default.</summary>
    public string AccountsApiBaseUrl { get; init; } = string.Empty;
}