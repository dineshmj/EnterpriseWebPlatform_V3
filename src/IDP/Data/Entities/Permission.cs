namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class Permission
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Relationships

    public ICollection<RolePermission> RolePermissions { get; set; }
        = new List<RolePermission>();
}