namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class RolePermission
{
    public long RoleId { get; set; }

    public long PermissionId { get; set; }

    // Relationships

    public Role Role { get; set; } = null!;

    public Permission Permission { get; set; } = null!;
}