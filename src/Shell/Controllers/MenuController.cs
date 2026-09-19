using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.BSS.BFFWeb.Data;
using EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Controllers;

[Authorize]
[ApiController]
[Route("bff/api/[controller]")]
public class MenuController : ControllerBase
{
    private readonly IMenuRepository _menuRepository;

    public MenuController(IMenuRepository menuRepository)
    {
        _menuRepository = menuRepository;
    }

    [HttpGet]
    public async Task<ActionResult<MenuResponse>> GetMenu()
    {
        var allClaims = User.Claims
            .Select(c => new
            {
                c.Type,
                c.Value,
                c.Issuer
            })
            .ToList();

        var userRoles = User
            .FindAll("role")
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var menuDetails = await _menuRepository.GetAuthorizedMenuAsync(
            userRoles,
            HttpContext.RequestAborted);

        var response = new MenuResponse
        {
            Microservices = menuDetails
                .GroupBy(menu => new
                {
                    menu.Microservice,
                    menu.BaseURL
                })
                .Select(microserviceGroup => new EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities.MicroserviceDto
                {
                    Name = microserviceGroup.Key.Microservice,
                    BaseURL = microserviceGroup.Key.BaseURL,
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
}