namespace EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

public sealed class ManagementAreaDto
{
    public string Name { get; init; } = string.Empty;

    public List<MenuItemDto> MenuItems { get; init; } = new List<MenuItemDto>();
}