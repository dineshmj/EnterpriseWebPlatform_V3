using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Repositories;

public interface IUserRepository
{
    Task<User?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<User?> FindBySubjectIdAsync(
        string subjectId,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> GetRolesByUserIdAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> GetPermissionsByUserIdAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task<User?> FindActiveBySubjectIdAsync(
        string subjectId,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);
}