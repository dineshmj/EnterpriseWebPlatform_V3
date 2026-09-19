using EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Data;

public sealed class MenuRepository : IMenuRepository
{
    private readonly MenuDbContext _context;

    public MenuRepository(MenuDbContext context)
    {
        _context = context;
    }

    // This query deliberately reads the Shell's navigation model only.
    // Authorization is based on the user's role claims for menu visibility.
    // The authoritative RBAC/ABAC/ReBAC checks remain in the BFF/API layers.
    private const string MenuSqlQuery = """
        SELECT
            ms."Name"              AS "Microservice",
            ms."BaseURL"           AS "BaseURL",
            ma."Name"              AS "ManagementAreaName",
            mi."TaskName"          AS "TaskName",
            mi."UrlRelativePath"   AS "UrlRelativePath",
            mi."IconName"          AS "IconName",
            mir."RoleShortName"    AS "RoleShortName"
        FROM
            "Microservices" ms
        INNER JOIN
            "ManagementAreas" ma
                ON ma."MicroserviceID" = ms."ID"
        INNER JOIN
            "MenuItems" mi
                ON mi."ManagementAreaID" = ma."ID"
        INNER JOIN
            "MenuItemsAndRoles" mir
                ON mir."MenuItemID" = mi."ID"
        WHERE
            mir."RoleShortName" IN ({0})
        ORDER BY
            ms."ID",
            ma."ID",
            mi."ID",
            mir."RoleShortName";
        """;

    public async Task<List<MenuDetail>> GetAuthorizedMenuAsync(
        IEnumerable<string> userRoles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRoles);

        var roles = userRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (roles.Length == 0)
        {
            return [];
        }

        var parameterPlaceholders = new string[roles.Length];
        var parameters = new object[roles.Length];

        for (var i = 0; i < roles.Length; i++)
        {
            parameterPlaceholders[i] = $"@p{i}";
            parameters[i] = roles[i];
        }

        var finalSql = string.Format(
            MenuSqlQuery,
            string.Join(", ", parameterPlaceholders));

        return await _context.MenuDetails
            .FromSqlRaw(finalSql, parameters)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}