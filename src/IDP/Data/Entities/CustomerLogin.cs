namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

// ┌─ :: ARCHITECTURE + SECURITY :: Customers sign in apart from staff ─────────────────────────────
// │ Why : A bank's customers and its staff are different populations with different rules: a
// │       customer has no role, branch or permission, and a customer sign-in must never open a staff
// │       screen (nor the reverse).
// │ How : Customers have their own table (customer_logins), their own sign-in pages and their own
// │       clients; the profile service answers a customer client only from this table and a staff
// │       client only from users. The customer number links the sign-in to Customer Onboarding's
// │       record, which links back by this subject ID.
// │ Ref : Features 2.1.6 | Authorization-Model 3 | OWASP A01 / A07 | ASVS V2.1, V2.2
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>A customer's own sign-in to "My EWP" (not a staff account).</summary>
public sealed class CustomerLogin
{
    public long Id { get; set; }

    /// <summary>The "sub" of the customer's tokens; never the database key.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>EWP's customer number (Customer Onboarding), e.g. CUST-100001 - also the user name.</summary>
    public string CustomerNumber { get; set; } = null!;

    /// <summary>The name the channel greets the customer with (first name).</summary>
    public string DisplayName { get; set; } = null!;

    /// <summary>The last three digits of the mobile on record: where one-time codes "go" (simulated SMS).</summary>
    public string MobileEnding { get; set; } = null!;

    public string HashedPassword { get; set; } = null!;

    public bool IsActive { get; set; }

    public int AccessFailedCount { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Why a one-time code was sent.</summary>
public enum CustomerCodePurpose
{
    /// <summary>Registration: proves the person holds the mobile on record.</summary>
    Registration,

    /// <summary>Sign-in with MFA on: the second factor.</summary>
    SignIn
}

/// <summary>
/// A six-digit one-time code waiting to be entered (5 minutes, 5 tries, once). Only a salted
/// SHA-256 hash is stored; the browser carries only this row's ID, in an encrypted cookie.
/// </summary>
public sealed class CustomerOneTimeCode
{
    public Guid Id { get; set; }

    public CustomerCodePurpose Purpose { get; set; }

    public string CustomerNumber { get; set; } = null!;

    /// <summary>The registration's typed e-mail address (Customer Onboarding checks it again when linking).</summary>
    public string? Email { get; set; }

    /// <summary>Who this code signs in (SignIn only).</summary>
    public Guid? SubjectId { get; set; }

    public string DisplayName { get; set; } = null!;

    public string MobileEnding { get; set; } = null!;

    public string Salt { get; set; } = null!;

    public string CodeHash { get; set; } = null!;

    public int FailedAttempts { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}