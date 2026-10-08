namespace EnterpriseWebPlatform.IdentityServer.Data.Entities;

/// <summary>
/// A user's authenticator (TOTP, e.g. Google Authenticator). The shared secret is stored ONLY
/// encrypted with the IDP's Data Protection key ring - a database reader cannot generate codes.
/// </summary>
public sealed class UserMfa
{
    public long UserId { get; set; }

    /// <summary>The TOTP secret, Data-Protection-encrypted (never stored readable).</summary>
    public string SecretProtected { get; set; } = null!;

    public DateTimeOffset EnrolledAt { get; set; }

    /// <summary>The 30-second time step of the last accepted code: a code is never accepted twice.</summary>
    public long LastUsedTimeStep { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>A single-use recovery code, for a lost phone. Stored only as a SHA-256 hash.</summary>
public sealed class UserMfaRecoveryCode
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string CodeHash { get; set; } = null!;

    public DateTimeOffset? UsedAt { get; set; }
}