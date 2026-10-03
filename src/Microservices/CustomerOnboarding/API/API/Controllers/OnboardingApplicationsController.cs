using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.API.Models;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.SubmitApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplication;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplications;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

[ApiController]
[Route("v1/onboarding/applications")]
public sealed class OnboardingApplicationsController : ControllerBase
{
    private readonly CreateOnboardingApplicationCommandHandler _createApplicationHandler;

    private readonly SubmitOnboardingApplicationCommandHandler _submitApplicationHandler;

    private readonly GetOnboardingApplicationQueryHandler _getApplicationHandler;

    private readonly GetOnboardingApplicationsQueryHandler _getApplicationsHandler;

    private readonly CustomerResourceAuthorization _resourceAuthorization;

    public OnboardingApplicationsController(
        CreateOnboardingApplicationCommandHandler createApplicationHandler,
        SubmitOnboardingApplicationCommandHandler submitApplicationHandler,
        GetOnboardingApplicationQueryHandler getApplicationHandler,
        GetOnboardingApplicationsQueryHandler getApplicationsHandler,
        CustomerResourceAuthorization resourceAuthorization)
    {
        _createApplicationHandler = createApplicationHandler;
        _submitApplicationHandler = submitApplicationHandler;
        _getApplicationHandler = getApplicationHandler;
        _getApplicationsHandler = getApplicationsHandler;
        _resourceAuthorization = resourceAuthorization;
    }

    [HttpGet]
    [Authorize(Policy = "OnboardingRead")]
    public async Task<ActionResult<PagedResult<OnboardingApplicationListItemDto>>> GetApplications(
        [FromQuery] GetOnboardingApplicationsQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _getApplicationsHandler.HandleAsync(
            query,
            _resourceAuthorization.GetScope(User),
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = "OnboardingRead")]
    public async Task<ActionResult<OnboardingApplicationDetailsDto>> GetApplication(
        long id,
        CancellationToken cancellationToken)
    {
        if (!await _resourceAuthorization.CanAccessApplicationAsync(User, id, cancellationToken))
        {
            return NotFound();
        }

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
        // The customer must be within the caller's scope; this also protects the
        // BFF's "existing customer" path from a client-supplied CustomerId.
        if (!await _resourceAuthorization.CanAccessCustomerAsync(User, request.CustomerId, cancellationToken))
        {
            return NotFound();
        }

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
        if (!await _resourceAuthorization.CanAccessApplicationAsync(User, id, cancellationToken))
        {
            return NotFound();
        }

        var command = new SubmitOnboardingApplicationCommand(
            id,
            request.ExpectedVersion);

        await _submitApplicationHandler.HandleAsync(
            command,
            cancellationToken);

        return NoContent();
    }
}
