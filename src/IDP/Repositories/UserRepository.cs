using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Repositories;

// ┌─ :: SECURITY :: Credential checks without enumeration, with lockout ───────────────────────────
// │ Why : Different answers or timings for unknown, inactive and locked users tell an attacker
// │       which accounts exist; unlimited attempts invite brute force.
// │ How : Unknown, inactive and locked-out users cost the same hash computation and get the same
// │       failure; after five wrong passwords the account is locked for 15 minutes.
// │ Ref : Features 2.1.3 | OWASP A07 | ASVS V2.2.1 (anti-automation), V2.1 | CWE-203, CWE-307
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class UserRepository : IUserRepository
{
    private readonly IdentityDbContext _context;
    private readonly IPasswordManager _passwordManager;

    public UserRepository(
        IdentityDbContext context,
        IPasswordManager passwordManager)
    {
        _context = context;
        _passwordManager = passwordManager;
    }

    public async Task<User?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.UserName == username,
                cancellationToken);
    }

    public async Task<User?> FindBySubjectIdAsync(
		string subjectId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(subjectId))
		{
			return null;
		}

		if (!Guid.TryParse(subjectId, out var subjectGuid))
		{
			return null;
		}

		return await _context.Users
			.AsNoTracking()
			.Include(user => user.EmploymentProfile)
				.ThenInclude(profile => profile.Department)
			.Include(user => user.EmploymentProfile)
				.ThenInclude(profile => profile.Branch)
			.SingleOrDefaultAsync(
				user => user.SubjectId == subjectGuid,
				cancellationToken);
	}

    public async Task<User?> FindActiveBySubjectIdAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return null;
        }

        if (!Guid.TryParse(subjectId, out var subjectGuid))
        {
            return null;
        }

        return await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user =>
                    user.SubjectId == subjectGuid &&
                    user.IsActive,
                cancellationToken);
    }

    public async Task<IEnumerable<string>> GetRolesByUserIdAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.UserId == userId &&
                userRole.Role.IsActive)
            .Select(userRole => userRole.Role.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<string>> GetPermissionsByUserIdAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.UserId == userId &&
                userRole.Role.IsActive)
            .SelectMany(userRole => userRole.Role.RolePermissions)
            .Where(rolePermission =>
                rolePermission.Permission.IsActive)
            .Select(rolePermission => rolePermission.Permission.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ValidateCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var user = await _context.Users
            .SingleOrDefaultAsync(
                candidate => candidate.UserName == username,
                cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Unknown, inactive and locked-out users cost the same hash computation
        // as a real check and get the same generic failure, so the response
        // neither reveals which usernames exist nor that an account is locked.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the user is unknown, inactive or locked, hash a dummy password anyway and fail the
        // │ same way - no timing or message reveals which accounts exist.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (user is null || !user.IsActive || user.LockoutEnd > now)
        {
            _passwordManager.VerifyAgainstDummyHash(password);
            return false;
        }

        var result = _passwordManager.VerifyPassword(
            user,
            user.HashedPassword,
            password);

        if (result == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ If this was the fifth wrong password, lock the account for 15 minutes (brute-force
            // │ protection).
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (user.AccessFailedCount >= MaxFailedAccessAttempts)
            {
                user.LockoutEnd = now.Add(LockoutDuration);
                user.AccessFailedCount = 0;
            }

            user.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return false;
        }

        // Success or SuccessRehashNeeded: both are valid credentials. A rehash is
        // required after a hasher upgrade; failing it would lock out valid users.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.HashedPassword = _passwordManager.HashPassword(user, password);
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private const int MaxFailedAccessAttempts = 5;

    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
}