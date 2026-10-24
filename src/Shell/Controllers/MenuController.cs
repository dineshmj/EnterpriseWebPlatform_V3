using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.BSS.BFFWeb.Configuration;
using EnterpriseWebPlatform.BSS.BFFWeb.Data;
using EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: The menu is data, filtered by role on the server ───────────────
// │ Why : Adding a context must not mean changing the Shell; and a menu built in the browser could
// │       show anything.
// │ How : The menu comes from the Shell database (adding a context adds rows); the server filters
// │       it by the roles in the person's token. The menu only shows the way - every API still
// │       decides on its own.
// │ Ref : Features 1.5.1, 3.9 | OWASP A01 (the UI is not the control) | Shell-Requirements.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
[Authorize]
[ApiController]
[Route("bff/api/[controller]")]
public class MenuController : ControllerBase
{
    private readonly IMenuRepository _menuRepository;
    private readonly MfeOrigins _mfeOrigins;
    private readonly ILogger<MenuController> _logger;

    public MenuController(IMenuRepository menuRepository, MfeOrigins mfeOrigins, ILogger<MenuController> logger)
    {
        _menuRepository = menuRepository;
        _mfeOrigins = mfeOrigins;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<MenuResponse>> GetMenu()
    {
        var userRoles = User
            .FindAll("role")
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var menuDetails = await _menuRepository.GetAuthorizedMenuAsync(
            userRoles,
            HttpContext.RequestAborted);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The address comes from configuration (MfeOrigins), never from the database. A menu
        // │ entry whose key has no address in this environment is left out (and logged): the
        // │ browser is never sent to an MFE the Shell's CSP would not frame.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var microservices = menuDetails
            .GroupBy(menu => new
            {
                menu.Microservice,
                menu.MfeKey
            })
            .ToList();
        var response = new MenuResponse
        {
            Microservices = microservices
                .Where(microserviceGroup => HasAddress(microserviceGroup.Key.Microservice, microserviceGroup.Key.MfeKey))
                .Select(microserviceGroup => new EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities.MicroserviceDto
                {
                    Name = microserviceGroup.Key.Microservice,
                    BaseURL = OriginOf(microserviceGroup.Key.MfeKey),
                    ManagementAreas = microserviceGroup
                        .GroupBy(menu => menu.ManagementAreaName)
                        .Select(managementAreaGroup => new ManagementAreaDto
                        {
                            Name = managementAreaGroup.Key,
                            MenuItems = managementAreaGroup
                                .Select(menu => new MenuItemDto
                                {
                                    TaskName = menu.TaskName,
                                    UrlRelativePath = menu.UrlRelativePath,
                                    IconName = menu.IconName
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };

        return Ok(response);
    }

    private bool HasAddress(string microservice, string mfeKey)
    {
        if (_mfeOrigins.TryGet(mfeKey, out _))
            return true;
        _logger.LogWarning(
            "Menu: {Microservice} names MFE key {MfeKey}, which has no address in MfeOrigins - left out of the menu.",
            microservice, mfeKey);
        return false;
    }

    private string OriginOf(string mfeKey) => _mfeOrigins.TryGet(mfeKey, out var origin) ? origin : string.Empty;
}