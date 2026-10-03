using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

[ApiController]
[Route("internal/v1/kyc/cases")]
public sealed class InternalKycCasesController(KycCaseService service) : ControllerBase
{
    /// <summary>
    /// Opens the KYC case for a submitted onboarding application. Called only by
    /// the CustomerKycSubscriber (pinned M2M client) when it consumes
    /// onboarding.application.submitted. Idempotent: one case per application, so
    /// a redelivered event returns the existing case (200) instead of a new one (201).
    /// </summary>
    [HttpPost("from-application-submitted")]
    [Authorize(Policy = "KycSubscriberWrite")]
    public async Task<IActionResult> CreateFromApplicationSubmitted(
        [FromBody] CreateKycCaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ApplicationId <= 0 ||
            string.IsNullOrWhiteSpace(request.ApplicationNumber) ||
            string.IsNullOrWhiteSpace(request.CustomerNumber))
        {
            return ValidationProblem("ApplicationId, ApplicationNumber and CustomerNumber are required.");
        }

        var result = await service.CreateOrGetAsync(request, cancellationToken);
        var response = new
        {
            kycCaseId = result.Case.Id,
            applicationId = result.Case.ApplicationId,
            applicationNumber = result.Case.ApplicationNumber,
            customerNumber = result.Case.CustomerNumber,
            status = result.Case.Status,
            created = result.Created
        };

        return result.Created ? Created($"/internal/v1/kyc/cases/{result.Case.Id}", response) : Ok(response);
    }
}
