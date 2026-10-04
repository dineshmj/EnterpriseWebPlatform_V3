using Microsoft.AspNetCore.Identity;

using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

public interface IPasswordManager
{
	string HashPassword(User user, string password);

	/// <summary>
	/// Success, SuccessRehashNeeded (valid, but the hash should be upgraded) or Failed.
	/// </summary>
	PasswordVerificationResult VerifyPassword(User user, string storedHash, string providedPassword);

	/// <summary>
	/// Performs a hash verification against a fixed dummy hash, so that an unknown
	/// or locked-out user costs the same time as a real check (no username enumeration).
	/// </summary>
	void VerifyAgainstDummyHash(string providedPassword);
}