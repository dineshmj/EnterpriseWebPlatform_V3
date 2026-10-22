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

    public DbSet<UserMfa> UserMfa => Set<UserMfa>();

    public DbSet<UserMfaRecoveryCode> UserMfaRecoveryCodes => Set<UserMfaRecoveryCode>();

    public DbSet<CustomerLogin> CustomerLogins => Set<CustomerLogin>();

    public DbSet<CustomerOneTimeCode> CustomerOneTimeCodes => Set<CustomerOneTimeCode>();

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
        ConfigureMfa(modelBuilder);
        ConfigureCustomers(modelBuilder);
    }

    // Customers' own sign-ins ("My EWP") and their one-time codes - apart from the staff tables.
    private static void ConfigureCustomers(ModelBuilder modelBuilder)
    {
        var login = modelBuilder.Entity<CustomerLogin>();
        login.ToTable("customer_logins");
        login.HasKey(x => x.Id);
        login.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        login.Property(x => x.SubjectId).HasColumnName("subject_id").IsRequired();
        login.HasIndex(x => x.SubjectId).IsUnique().HasDatabaseName("ux_customer_logins_subject_id");
        login.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(20).IsRequired();
        login.HasIndex(x => x.CustomerNumber).IsUnique().HasDatabaseName("ux_customer_logins_customer_number");
        login.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
        login.Property(x => x.MobileEnding).HasColumnName("mobile_ending").HasMaxLength(3).IsRequired();
        login.Property(x => x.HashedPassword).HasColumnName("hashed_password").HasMaxLength(500).IsRequired();
        login.Property(x => x.IsActive).HasColumnName("is_active");
        login.Property(x => x.AccessFailedCount).HasColumnName("access_failed_count");
        login.Property(x => x.LockoutEnd).HasColumnName("lockout_end");
        login.Property(x => x.CreatedAt).HasColumnName("created_at");
        login.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        var code = modelBuilder.Entity<CustomerOneTimeCode>();
        code.ToTable("customer_one_time_codes");
        code.HasKey(x => x.Id);
        code.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        code.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(20)
            .HasConversion(v => v == CustomerCodePurpose.SignIn ? "SIGN_IN" : "REGISTRATION",
                           v => v == "SIGN_IN" ? CustomerCodePurpose.SignIn : CustomerCodePurpose.Registration);
        code.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(20).IsRequired();
        code.Property(x => x.Email).HasColumnName("email").HasMaxLength(254);
        code.Property(x => x.SubjectId).HasColumnName("subject_id");
        code.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
        code.Property(x => x.MobileEnding).HasColumnName("mobile_ending").HasMaxLength(3).IsRequired();
        code.Property(x => x.Salt).HasColumnName("salt").HasMaxLength(32).IsRequired();
        code.Property(x => x.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
        code.Property(x => x.FailedAttempts).HasColumnName("failed_attempts");
        code.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        code.Property(x => x.UsedAt).HasColumnName("used_at");
        code.Property(x => x.CreatedAt).HasColumnName("created_at");
        code.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_customer_one_time_codes_expires_at");
    }

    // Multi-factor authentication (TOTP): one authenticator per user, and its recovery codes.
    private static void ConfigureMfa(ModelBuilder modelBuilder)
    {
        var mfa = modelBuilder.Entity<UserMfa>();
        mfa.ToTable("user_mfa");
        mfa.HasKey(x => x.UserId);
        mfa.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
        mfa.Property(x => x.SecretProtected).HasColumnName("secret_protected").IsRequired();
        mfa.Property(x => x.EnrolledAt).HasColumnName("enrolled_at");
        mfa.Property(x => x.LastUsedTimeStep).HasColumnName("last_used_time_step");
        mfa.HasOne(x => x.User).WithOne().HasForeignKey<UserMfa>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var codes = modelBuilder.Entity<UserMfaRecoveryCode>();
        codes.ToTable("user_mfa_recovery_codes");
        codes.HasKey(x => x.Id);
        codes.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        codes.Property(x => x.UserId).HasColumnName("user_id");
        codes.Property(x => x.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
        codes.Property(x => x.UsedAt).HasColumnName("used_at");
        codes.HasIndex(x => x.UserId).HasDatabaseName("ix_user_mfa_recovery_codes_user_id");
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

        entity.Property(x => x.AccessFailedCount)
            .HasColumnName("access_failed_count")
            .IsRequired();

        entity.Property(x => x.LockoutEnd)
            .HasColumnName("lockout_end")
            .HasColumnType("timestamp with time zone");

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

        entity.Property(x => x.City)
            .HasColumnName("city")
            .HasMaxLength(100);

        entity.Property(x => x.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(2);

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

        entity.Property(x => x.LanId)
            .HasColumnName("lan_id")
            .HasMaxLength(20)
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
}