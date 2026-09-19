using EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Data;

public interface IMenuRepository
{
    Task<List<MenuDetail>> GetAuthorizedMenuAsync(
        IEnumerable<string> userRoles,
        CancellationToken cancellationToken = default);
}
