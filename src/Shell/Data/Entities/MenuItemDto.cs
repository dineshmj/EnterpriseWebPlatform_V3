namespace EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

public sealed class MenuItemDto
{
    public string TaskName { get; init; } = string.Empty;

    public string UrlRelativePath { get; init; } = string.Empty;

    public string IconName { get; init; } = string.Empty;
}