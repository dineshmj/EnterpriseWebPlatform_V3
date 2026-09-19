namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

public sealed class UserEmploymentProfile
{
    /// <summary>
    /// Also serves as the primary key and FK to Users.Id.
    /// </summary>
    public long UserId { get; set; }

    public string EmployeeId { get; set; } = null!;

    public long DepartmentId { get; set; }

    public long BranchId { get; set; }

    public string EmploymentType { get; set; } = null!;

    public int ClearanceLevel { get; set; }

    public long? ManagerUserId { get; set; }

    // Relationships

    public User User { get; set; } = null!;

    public Department Department { get; set; } = null!;

    public Branch Branch { get; set; } = null!;

    public User? Manager { get; set; }

    //public ICollection<UserEmploymentProfile> ManagedEmployees { get; set; }
    //    = new List<UserEmploymentProfile>();
}