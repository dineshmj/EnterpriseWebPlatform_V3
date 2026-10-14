using Microsoft.AspNetCore.Identity;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

// ┌─ :: SECURITY :: Password hashing ──────────────────────────────────────────────────────────────
// │ Why : Stored passwords must be useless to anyone who copies the database.
// │ How : ASP.NET Core Identity's password hasher (PBKDF2 with a per-user salt and many
// │       iterations); VerifyAgainstDummyHash spends the same time for a user that does not exist.
// │ Ref : Features 2.1.3 | OWASP Password Storage Cheat Sheet | ASVS V2.4 | CWE-916
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PasswordManager
    : IPasswordManager
{
    private readonly PasswordHasher<object> _passwordHasher = new();

    private readonly string _dummyHash;

    public PasswordManager()
    {
        _dummyHash = _passwordHasher.HashPassword(new object(), Guid.NewGuid().ToString("N"));
    }

    public string HashPassword(User user, string password)
    {
        return _passwordHasher.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyPassword(User user, string storedHash, string providedPassword)
    {
        return _passwordHasher.VerifyHashedPassword(user, storedHash, providedPassword);
    }

    public void VerifyAgainstDummyHash(string providedPassword)
    {
        _passwordHasher.VerifyHashedPassword(new object(), _dummyHash, providedPassword);
    }
}