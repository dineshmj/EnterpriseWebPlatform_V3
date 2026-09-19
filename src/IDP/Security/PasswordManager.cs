using System.Text;

using Microsoft.AspNetCore.Identity;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

public sealed class PasswordManager
    : IPasswordManager
{
    private readonly PasswordHasher<object> _passwordHasher = new();

    public string HashPassword(User user, string password)
    {
        return _passwordHasher.HashPassword(user, password);
    }

    public bool VerifyPassword(User user, string storedHash, string providedPassword)
    {
        var result = _passwordHasher.VerifyHashedPassword(user, storedHash, providedPassword);

        return result == PasswordVerificationResult.Success;
    }
}