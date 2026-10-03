namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class Branch
{
    public long Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Region { get; set; }

    // Location of the branch. Issued as the branch_city / branch_country_code
    // claims, which business contexts use for branch-scoped (ABAC) access.
    public string? City { get; set; }

    public string? CountryCode { get; set; }

    public bool IsActive { get; set; }

    // Relationships

    public ICollection<UserEmploymentProfile> EmploymentProfiles { get; set; }
        = new List<UserEmploymentProfile>();
}