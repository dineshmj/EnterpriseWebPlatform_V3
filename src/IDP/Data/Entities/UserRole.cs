namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class UserRole
{
    public long UserId { get; set; }

    public long RoleId { get; set; }

    // Relationships

    public User User { get; set; } = null!;

    public Role Role { get; set; } = null!;
}