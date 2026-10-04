using Microsoft.AspNetCore.Identity;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

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