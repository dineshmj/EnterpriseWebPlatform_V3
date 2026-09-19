namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class Branch
{
    public long Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Region { get; set; }

    public bool IsActive { get; set; }

    // Relationships

    public ICollection<UserEmploymentProfile> EmploymentProfiles { get; set; }
        = new List<UserEmploymentProfile>();
}