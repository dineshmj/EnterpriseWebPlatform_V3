using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomers;
using EnterpriseWebPlatform.CustomerOnboarding.API.Models;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

[ApiController]
[Route("v1/customers")]
public sealed class CustomersController : ControllerBase
{
    private readonly CreateCustomerCommandHandler _createCustomerHandler;
    private readonly UpdateCustomerCommandHandler _updateCustomerHandler;
    private readonly GetCustomerQueryHandler _getCustomerHandler;
    private readonly GetCustomersQueryHandler _getCustomersHandler;

    public CustomersController(
        CreateCustomerCommandHandler createCustomerHandler,
        UpdateCustomerCommandHandler updateCustomerHandler,
        GetCustomerQueryHandler getCustomerHandler,
        GetCustomersQueryHandler getCustomersHandler)
    {
        _createCustomerHandler = createCustomerHandler;
        _updateCustomerHandler = updateCustomerHandler;
        _getCustomerHandler = getCustomerHandler;
        _getCustomersHandler = getCustomersHandler;
    }

    [HttpGet]
    [Authorize(Policy = "CustomerRead")]
    public async Task<ActionResult<PagedResult<CustomerListItemDto>>> GetCustomers(
        [FromQuery] GetCustomersQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _getCustomersHandler.HandleAsync(
            query,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = "CustomerRead")]
    public async Task<ActionResult<CustomerDetailsDto>> GetCustomer(
        long id,
        CancellationToken cancellationToken)
    {
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
        var command = new CreateCustomerCommand(
            request.CustomerNumber,
            request.FirstName,
            request.LastName,
            request.Email,
            request.PhoneNumber,
            request.CustomerType,
            request.SubjectId,
            request.BranchId);

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