using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

public sealed record OpenAccountApplicationRequest(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    long ComplianceCaseId,
    string BranchCode,
    string? InitiatedByUserId,
    string? ComplianceApprovedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    ApplicantNameContract? Applicant);

/// <summary>The applicant's name as Compliance cleared it: the account holder.</summary>
public sealed record ApplicantNameContract(string? FirstName, string? LastName);

/// <summary>
/// Machine-only endpoint: called by the AccountApplicationOpeningSubscriber (pinned M2M
/// client) when it consumes compliance.case.approved. Idempotent: a redelivered event
/// returns the existing application (200) instead of a new one (201).
/// </summary>
[ApiController]
[Route("internal/v1/accounts/applications")]
public sealed class InternalAccountApplicationsController(OpenAccountApplicationCommandHandler handler) : ControllerBase
{
    [HttpPost("from-compliance-approved")]
    [Authorize(Policy = "AccountApplicationOpeningSubscriberWrite")]
    public async Task<IActionResult> Open([FromBody] OpenAccountApplicationRequest request, CancellationToken cancellationToken)
    {
        if (request.CausationId == Guid.Empty)
            return ValidationProblem("CausationId (the triggering MessageId) is required.");

        OpenAccountApplicationResult result;
        try
        {
            var holderName = HolderName.Create(request.Applicant?.FirstName, request.Applicant?.LastName);

            result = await handler.HandleAsync(
                new OpenAccountApplicationCommand(
                    request.ApplicationRef, request.ApplicationNumber, request.CustomerNumber, request.ComplianceCaseId,
                    request.BranchCode, request.InitiatedByUserId, request.ComplianceApprovedByUserId,
                    request.WorkflowId, request.CorrelationId, request.CausationId, holderName),
                cancellationToken);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }

        var body = new { accountApplicationId = result.AccountApplicationId, status = result.Status, created = result.Created };
        return result.Created ? Created($"/v1/accounts/applications/{result.AccountApplicationId}", body) : Ok(body);
    }
}