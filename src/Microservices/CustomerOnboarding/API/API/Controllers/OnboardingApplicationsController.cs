using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.SubmitApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplications;
using EnterpriseWebPlatform.CustomerOnboarding.API.Models;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

[ApiController]
[Route("v1/onboarding/applications")]
public sealed class OnboardingApplicationsController : ControllerBase
{
    private readonly CreateOnboardingApplicationCommandHandler _createApplicationHandler;
    private readonly SubmitOnboardingApplicationCommandHandler _submitApplicationHandler;
    private readonly GetOnboardingApplicationQueryHandler _getApplicationHandler;
    private readonly GetOnboardingApplicationsQueryHandler _getApplicationsHandler;

    public OnboardingApplicationsController(
        CreateOnboardingApplicationCommandHandler createApplicationHandler,
        SubmitOnboardingApplicationCommandHandler submitApplicationHandler,
        GetOnboardingApplicationQueryHandler getApplicationHandler,
        GetOnboardingApplicationsQueryHandler getApplicationsHandler)
    {
        _createApplicationHandler = createApplicationHandler;
        _submitApplicationHandler = submitApplicationHandler;
        _getApplicationHandler = getApplicationHandler;
        _getApplicationsHandler = getApplicationsHandler;
    }

    [HttpGet]
    [Authorize(Policy = "OnboardingRead")]
    public async Task<ActionResult<PagedResult<OnboardingApplicationListItemDto>>> GetApplications(
        [FromQuery] GetOnboardingApplicationsQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _getApplicationsHandler.HandleAsync(
            query,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = "OnboardingRead")]
    public async Task<ActionResult<OnboardingApplicationDetailsDto>> GetApplication(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await _getApplicationHandler.HandleAsync(
            new GetOnboardingApplicationQuery(id),
            cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "OnboardingWrite")]
    public async Task<ActionResult<CreateOnboardingApplicationResult>> CreateApplication(
        [FromBody] CreateOnboardingApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateOnboardingApplicationCommand(
            request.CustomerId,
            request.ApplicationNumber);

        var result = await _createApplicationHandler.HandleAsync(
            command,
            cancellationToken);

        return CreatedAtAction(
            nameof(GetApplication),
            new { id = result.ApplicationId },
            result);
    }

    [HttpPost("{id:long}/submit")]
    [Authorize(Policy = "OnboardingWrite")]
    public async Task<IActionResult> SubmitApplication(
        long id,
        [FromBody] SubmitOnboardingApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SubmitOnboardingApplicationCommand(
            id,
            request.ExpectedVersion);

        await _submitApplicationHandler.HandleAsync(
            command,
            cancellationToken);

        return NoContent();
    }
}