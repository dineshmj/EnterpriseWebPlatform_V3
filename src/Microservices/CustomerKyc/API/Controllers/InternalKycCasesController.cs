using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

[ApiController]
[Route("internal/v1/kyc/cases")]
public sealed class InternalKycCasesController(OpenKycCaseCommandHandler openKycCaseHandler) : ControllerBase
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
        [FromBody] OpenKycCaseRequest request,
        CancellationToken cancellationToken)
    {
        OpenKycCaseResult result;
        try
        {
            result = await openKycCaseHandler.HandleAsync(
                new OpenKycCaseCommand(
                    request.ApplicationRef,
                    request.ApplicationNumber,
                    request.CustomerNumber,
                    request.BranchCode,
                    request.InitiatedByUserId,
                    request.WorkflowId,
                    request.CorrelationId,
                    request.CausationId),
                cancellationToken);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }

        var response = new
        {
            kycCaseId = result.KycCaseId,
            applicationRef = result.ApplicationRef,
            branchCode = result.BranchCode,
            applicationNumber = result.ApplicationNumber,
            customerNumber = result.CustomerNumber,
            status = result.Status,
            created = result.Created
        };

        return result.Created ? Created($"/internal/v1/kyc/cases/{result.KycCaseId}", response) : Ok(response);
    }
}
