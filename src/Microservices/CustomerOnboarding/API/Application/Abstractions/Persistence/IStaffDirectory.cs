namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

/// <summary>
/// The context's staff directory: remembers a staff member's LAN ID (from their token) so
/// published events can name the initiator by it. Identity and every rule stay on the
/// subject ID.
/// </summary>
public interface IStaffDirectory
{
    Task RememberAsync(string? userId, string? lanId, CancellationToken cancellationToken);
}