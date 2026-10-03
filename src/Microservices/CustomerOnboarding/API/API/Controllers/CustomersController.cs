using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.API.Models;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomers;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

[ApiController]
[Route("v1/customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly CreateCustomerCommandHandler _createCustomerHandler;

    private readonly UpdateCustomerCommandHandler _updateCustomerHandler;

    private readonly GetCustomerQueryHandler _getCustomerHandler;

    private readonly GetCustomersQueryHandler _getCustomersHandler;

    private readonly CustomerResourceAuthorization _resourceAuthorization;

    public CustomersController(
        CreateCustomerCommandHandler createCustomerHandler,
        UpdateCustomerCommandHandler updateCustomerHandler,
        GetCustomerQueryHandler getCustomerHandler,
        GetCustomersQueryHandler getCustomersHandler,
        CustomerResourceAuthorization resourceAuthorization)
    {
        _createCustomerHandler = createCustomerHandler;
        _updateCustomerHandler = updateCustomerHandler;
        _getCustomerHandler = getCustomerHandler;
        _getCustomersHandler = getCustomersHandler;
        _resourceAuthorization = resourceAuthorization;
    }

    [HttpGet]
    [Authorize(Policy = "CustomerRead")]
    public async Task<ActionResult<PagedResult<CustomerListItemDto>>> GetCustomers(
        [FromQuery] GetCustomersQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _getCustomersHandler.HandleAsync(
            query,
            _resourceAuthorization.GetScope(User),
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = "CustomerRead")]
    public async Task<ActionResult<CustomerDetailsDto>> GetCustomer(
        long id,
        CancellationToken cancellationToken)
    {
        // 404 rather than 403, so a caller cannot probe customers outside their scope.
        if (!await _resourceAuthorization.CanAccessCustomerAsync(User, id, cancellationToken))
        {
            return NotFound();
        }

        var result = await _getCustomerHandler.HandleAsync(
            new GetCustomerQuery(id),
            cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "CustomerWrite")]
    public async Task<ActionResult<CreateCustomerResult>> CreateCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var address = request.ResidentialAddress!;

        // An agent may only create customers within their own branch scope;
        // otherwise they would create customers they can never see again.
        if (!_resourceAuthorization.GetScope(User).AllowsLocation(address.City, address.CountryCode))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Outside branch scope",
                detail: "The customer's residential address is outside your branch's city.");
        }

        var command = new CreateCustomerCommand(
            request.FirstName,
            request.LastName,
            request.Email,
            request.PhoneNumber,
            request.CustomerType,
            new ResidentialAddress(
                address.AddressLine1,
                address.AddressLine2,
                address.City,
                address.State,
                address.PostalCode,
                address.CountryCode));

        var result = await _createCustomerHandler.HandleAsync(
            command,
            cancellationToken);

        return CreatedAtAction(
            nameof(GetCustomer),
            new { id = result.CustomerId },
            result);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = "CustomerWrite")]
    public async Task<IActionResult> UpdateCustomer(
        long id,
        [FromBody] UpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _resourceAuthorization.CanAccessCustomerAsync(User, id, cancellationToken))
        {
            return NotFound();
        }

        var command = new UpdateCustomerCommand(
            id,
            request.FirstName,
            request.LastName,
            request.Email,
            request.PhoneNumber,
            request.ExpectedVersion);

        await _updateCustomerHandler.HandleAsync(
            command,
            cancellationToken);

        return NoContent();
    }
}
