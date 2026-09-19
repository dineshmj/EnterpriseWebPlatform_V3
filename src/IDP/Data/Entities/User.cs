namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class User
{
    public long Id { get; set; }

    /// <summary>
    /// Stable, opaque identifier exposed as the OIDC/JWT "sub" claim.
    /// This must not expose the internal database primary key.
    /// </summary>
    public Guid SubjectId { get; set; }

    public string FirstName { get; set; } = null!;

    public string LastName { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string UserName { get; set; } = null!;

    public string HashedPassword { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Relationships

    public ICollection<UserRole> UserRoles { get; set; }
        = new List<UserRole>();

    public UserEmploymentProfile? EmploymentProfile { get; set; }

    /// <summary>
    /// Relationships where this user is the subject.
    /// </summary>
    public ICollection<UserRelationship> Relationships { get; set; }
        = new List<UserRelationship>();

    /// <summary>
    /// Users for whom this user is the manager.
    /// </summary>
    public ICollection<UserEmploymentProfile> ManagedEmployees { get; set; }
        = new List<UserEmploymentProfile>();
}