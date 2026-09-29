using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

[ApiController]
[Route("internal/v1/kyc/cases")]
public sealed class InternalKycCasesController(KycCaseService service) : ControllerBase
{
    [HttpPost("from-customer-created")]
    [Authorize(Policy = "KycSubscriberWrite")]
    public async Task<IActionResult> CreateFromCustomerCreated(
        [FromBody] CreateKycCaseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerNumber))
            return ValidationProblem("CustomerId and CustomerNumber are required.");

        var result = await service.CreateOrGetAsync(request, cancellationToken);
        var response = new
        {
            kycCaseId = result.Case.Id,
            customerNumber = result.Case.CustomerNumber,
            status = result.Case.Status,
            created = result.Created
        };

        return result.Created ? Created($"/internal/v1/kyc/cases/{result.Case.Id}", response) : Ok(response);
    }
}