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
            ms.name              AS "Microservice",
            ms.base_url          AS "BaseURL",
            ma.name              AS "ManagementAreaName",
            mi.task_name         AS "TaskName",
            mi.url_relative_path AS "UrlRelativePath",
            mi.icon_name         AS "IconName"
        FROM
            microservices ms
        INNER JOIN
            management_areas ma
                ON ma.microservice_id = ms.id
        INNER JOIN
            menu_items mi
                ON mi.management_area_id = ma.id
        WHERE EXISTS
        (
            SELECT 1
            FROM
                menu_items_and_roles mir
            WHERE
                mir.menu_item_id = mi.id
                AND mir.role_short_name IN ({0})
        )
        ORDER BY
            ms.id,
            ma.id,
            mi.id;
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