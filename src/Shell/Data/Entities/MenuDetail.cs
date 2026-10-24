namespace EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

public sealed class MenuDetail
{
    public string Microservice { get; set; } = string.Empty;

    /// <summary>The MFE's key in the MfeOrigins setting - never an address.</summary>
    public string MfeKey { get; set; } = string.Empty;

    public string ManagementAreaName { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public string UrlRelativePath { get; set; } = string.Empty;

    public string IconName { get; set; } = string.Empty;
}