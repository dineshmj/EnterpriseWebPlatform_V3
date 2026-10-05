using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

[ApiController]
[Route("internal/v1/kyc/cases")]
public sealed class InternalKycCasesController(OpenKycCaseCommandHandler openKycCaseHandler) : ControllerBase
{
    /// <summary>
    /// Opens the KYC case for a submitted onboarding application. Called only by
    /// the KycCaseOpeningSubscriber (pinned M2M client) when it consumes
    /// onboarding.application.submitted. Idempotent: one case per application, so
    /// a redelivered event returns the existing case (200) instead of a new one (201).
    /// </summary>
    [HttpPost("from-application-submitted")]
    [Authorize(Policy = "KycCaseOpeningSubscriberWrite")]
    public async Task<IActionResult> CreateFromApplicationSubmitted(
        [FromBody] OpenKycCaseRequest request,
        CancellationToken cancellationToken)
    {
        OpenKycCaseResult result;
        try
        {
            var address = request.Applicant?.ResidentialAddress;
            var applicant = Applicant.Create(
                request.Applicant?.FirstName,
                request.Applicant?.LastName,
                address?.AddressLine1,
                address?.AddressLine2,
                address?.City,
                address?.State,
                address?.PostalCode,
                address?.CountryCode);

            Guid EvidenceOfType(string documentType) =>
                request.EvidenceDocuments?
                    .Where(x => string.Equals(x.DocumentType, documentType, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.DocumentId)
                    .SingleOrDefault() ?? Guid.Empty;

            result = await openKycCaseHandler.HandleAsync(
                new OpenKycCaseCommand(
                    request.ApplicationRef,
                    request.ApplicationNumber,
                    request.CustomerNumber,
                    request.BranchCode,
                    request.InitiatedByUserId,
                    request.WorkflowId,
                    request.CorrelationId,
                    request.CausationId,
                    applicant,
                    EvidenceOfType("KYCProof"),
                    EvidenceOfType("TaxProof")),
                cancellationToken);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (InvalidOperationException)
        {
            // SingleOrDefault: more than one document of a type.
            return ValidationProblem("The application names more than one document of the same type.");
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