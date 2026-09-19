using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

public interface IPasswordManager
{
	string HashPassword(User user, string password);

	bool VerifyPassword(User user, string storedHash, string providedPassword);
}