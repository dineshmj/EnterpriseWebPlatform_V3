using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Controllers;

public sealed record OpenComplianceCaseRequest(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    long KycCaseId,
    string BranchCode,
    string? InitiatedByUserId,
    string? KycIdentityDecidedByUserId,
    string? KycDocumentDecidedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    ApplicantContract? Applicant);

/// <summary>The applicant as KYC verified them (name and residential address).</summary>
public sealed record ApplicantContract(string? FirstName, string? LastName, AddressContract? ResidentialAddress);

public sealed record AddressContract(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode);

/// <summary>
/// Machine-only endpoint: called by the ComplianceCaseOpeningSubscriber (pinned M2M
/// client) when it consumes kyc.case.approved. Idempotent: a redelivered event
/// returns the existing case (200) instead of a new one (201).
/// </summary>
[ApiController]
[Route("internal/v1/compliance/cases")]
public sealed class InternalComplianceCasesController(OpenComplianceCaseCommandHandler handler) : ControllerBase
{
    [HttpPost("from-kyc-approved")]
    [Authorize(Policy = "ComplianceCaseOpeningSubscriberWrite")]
    public async Task<IActionResult> Open([FromBody] OpenComplianceCaseRequest request, CancellationToken cancellationToken)
    {
        if (request.CausationId == Guid.Empty)
            return ValidationProblem("CausationId (the triggering MessageId) is required.");

        OpenComplianceCaseResult result;
        try
        {
            var address = request.Applicant?.ResidentialAddress;
            var applicant = Applicant.Create(
                request.Applicant?.FirstName, request.Applicant?.LastName,
                address?.AddressLine1, address?.AddressLine2, address?.City,
                address?.State, address?.PostalCode, address?.CountryCode);

            result = await handler.HandleAsync(
                new OpenComplianceCaseCommand(
                    request.ApplicationRef, request.ApplicationNumber, request.CustomerNumber, request.KycCaseId,
                    request.BranchCode, request.InitiatedByUserId, request.KycIdentityDecidedByUserId,
                    request.KycDocumentDecidedByUserId, request.WorkflowId, request.CorrelationId, request.CausationId,
                    applicant),
                cancellationToken);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }

        var body = new { complianceCaseId = result.ComplianceCaseId, status = result.Status, created = result.Created };
        return result.Created ? Created($"/v1/compliance/cases/{result.ComplianceCaseId}", body) : Ok(body);
    }
}