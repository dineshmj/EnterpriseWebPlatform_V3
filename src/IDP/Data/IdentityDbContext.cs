using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Data;

public sealed class IdentityDbContext : DbContext
{
    public IdentityDbContext(
        DbContextOptions<IdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<UserEmploymentProfile> UserEmploymentProfiles => Set<UserEmploymentProfile>();
    public DbSet<UserRelationship> UserRelationships => Set<UserRelationship>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUser(modelBuilder);
        ConfigureRole(modelBuilder);
        ConfigurePermission(modelBuilder);
        ConfigureUserRole(modelBuilder);
        ConfigureRolePermission(modelBuilder);
        ConfigureBranch(modelBuilder);
        ConfigureDepartment(modelBuilder);
        ConfigureUserEmploymentProfile(modelBuilder);
        ConfigureUserRelationship(modelBuilder);
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<User>();

        entity.ToTable("users");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.SubjectId)
			.HasColumnName("subject_id")
			.IsRequired();

        entity.HasIndex(x => x.SubjectId)
            .IsUnique();

        entity.Property(x => x.FirstName)
            .HasColumnName("first_name")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.LastName)
            .HasColumnName("last_name")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.Email)
            .HasColumnName("email")
            .HasMaxLength(200)
            .IsRequired();

        entity.Property(x => x.UserName)
            .HasColumnName("user_name")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.HashedPassword)
            .HasColumnName("hashed_password")
            .HasMaxLength(500)
            .IsRequired();

        entity.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        entity.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        entity.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        entity.HasIndex(x => x.SubjectId)
            .IsUnique()
            .HasDatabaseName("ux_users_subject_id");

        entity.HasIndex(x => x.Email)
            .IsUnique()
            .HasDatabaseName("ux_users_email");

        entity.HasIndex(x => x.UserName)
            .IsUnique()
            .HasDatabaseName("ux_users_user_name");

        entity.HasIndex(x => x.IsActive)
            .HasDatabaseName("ix_users_is_active");
    }

    private static void ConfigureRole(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Role>();

        entity.ToTable("roles");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.Code)
            .HasColumnName("code")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        entity.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        entity.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        entity.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("ux_roles_name");

        entity.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("ux_roles_code");

        entity.HasIndex(x => x.IsActive)
            .HasDatabaseName("ix_roles_is_active");
    }

    private static void ConfigurePermission(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Permission>();

        entity.ToTable("permissions");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .IsRequired();

        entity.Property(x => x.Code)
            .HasColumnName("code")
            .HasMaxLength(150)
            .IsRequired();

        entity.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        entity.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        entity.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        entity.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("ux_permissions_name");

        entity.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("ux_permissions_code");

        entity.HasIndex(x => x.IsActive)
            .HasDatabaseName("ix_permissions_is_active");
    }

    private static void ConfigureUserRole(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserRole>();

        entity.ToTable("user_roles");

        entity.HasKey(x => new
        {
            x.UserId,
            x.RoleId
        });

        entity.Property(x => x.UserId)
            .HasColumnName("user_id");

        entity.Property(x => x.RoleId)
            .HasColumnName("role_id");

        entity.HasOne(x => x.User)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Role)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.RoleId)
            .HasDatabaseName("ix_user_roles_role_id");
    }

    private static void ConfigureRolePermission(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RolePermission>();

        entity.ToTable("role_permissions");

        entity.HasKey(x => new
        {
            x.RoleId,
            x.PermissionId
        });

        entity.Property(x => x.RoleId)
            .HasColumnName("role_id");

        entity.Property(x => x.PermissionId)
            .HasColumnName("permission_id");

        entity.HasOne(x => x.Role)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Permission)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => x.PermissionId)
            .HasDatabaseName("ix_role_permissions_permission_id");
    }

    private static void ConfigureBranch(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Branch>();

        entity.ToTable("branches");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.Code)
            .HasColumnName("code")
            .HasMaxLength(20)
            .IsRequired();

        entity.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .IsRequired();

        entity.Property(x => x.Region)
            .HasColumnName("region")
            .HasMaxLength(100);

        entity.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        entity.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("ux_branches_code");

        entity.HasIndex(x => x.IsActive)
            .HasDatabaseName("ix_branches_is_active");
    }

    private static void ConfigureDepartment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Department>();

        entity.ToTable("departments");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.Code)
            .HasColumnName("code")
            .HasMaxLength(50)
            .IsRequired();

        entity.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(150)
            .IsRequired();

        entity.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        entity.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("ux_departments_code");

        entity.HasIndex(x => x.IsActive)
            .HasDatabaseName("ix_departments_is_active");
    }

    private static void ConfigureUserEmploymentProfile(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserEmploymentProfile>();

        entity.ToTable("user_employment_profiles");

        entity.HasKey(x => x.UserId);

        entity.Property(x => x.UserId)
            .HasColumnName("user_id");

        entity.Property(x => x.EmployeeId)
            .HasColumnName("employee_id")
            .HasMaxLength(50)
            .IsRequired();

        entity.Property(x => x.DepartmentId)
            .HasColumnName("department_id")
            .IsRequired();

        entity.Property(x => x.BranchId)
            .HasColumnName("branch_id")
            .IsRequired();

        entity.Property(x => x.EmploymentType)
            .HasColumnName("employment_type")
            .HasMaxLength(50)
            .IsRequired();

        entity.Property(x => x.ClearanceLevel)
            .HasColumnName("clearance_level")
            .IsRequired();

        entity.Property(x => x.ManagerUserId)
            .HasColumnName("manager_user_id");

        entity.HasOne(x => x.User)
            .WithOne(x => x.EmploymentProfile)
            .HasForeignKey<UserEmploymentProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(x => x.Department)
            .WithMany(x => x.EmploymentProfiles)
            .HasForeignKey(x => x.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(x => x.Branch)
            .WithMany(x => x.EmploymentProfiles)
            .HasForeignKey(x => x.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(x => x.Manager)
            .WithMany(x => x.ManagedEmployees)
            .HasForeignKey(x => x.ManagerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(x => x.EmployeeId)
            .IsUnique()
            .HasDatabaseName("ux_user_employment_profiles_employee_id");

        entity.HasIndex(x => x.DepartmentId)
            .HasDatabaseName("ix_user_employment_profiles_department_id");

        entity.HasIndex(x => x.BranchId)
            .HasDatabaseName("ix_user_employment_profiles_branch_id");

        entity.HasIndex(x => x.ManagerUserId)
            .HasDatabaseName("ix_user_employment_profiles_manager_user_id");

        entity.HasIndex(x => new
        {
            x.DepartmentId,
            x.BranchId
        })
        .HasDatabaseName("ix_user_employment_profiles_department_branch");
    }

    private static void ConfigureUserRelationship(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserRelationship>();

        entity.ToTable("user_relationships");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        entity.Property(x => x.SubjectUserId)
            .HasColumnName("subject_user_id")
            .IsRequired();

        entity.Property(x => x.RelationshipType)
            .HasColumnName("relationship_type")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.ResourceType)
            .HasColumnName("resource_type")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.ResourceId)
            .HasColumnName("resource_id")
            .HasMaxLength(100)
            .IsRequired();

        entity.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        entity.HasOne(x => x.SubjectUser)
            .WithMany(x => x.Relationships)
            .HasForeignKey(x => x.SubjectUserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(x => new
        {
            x.SubjectUserId,
            x.RelationshipType,
            x.ResourceType,
            x.ResourceId
        })
        .IsUnique()
        .HasDatabaseName("ux_user_relationships_subject_relationship_resource");

        entity.HasIndex(x => new
        {
            x.ResourceType,
            x.ResourceId
        })
        .HasDatabaseName("ix_user_relationships_resource");

        entity.HasIndex(x => new
        {
            x.SubjectUserId,
            x.RelationshipType
        })
        .HasDatabaseName("ix_user_relationships_subject_relationship");
    }
}