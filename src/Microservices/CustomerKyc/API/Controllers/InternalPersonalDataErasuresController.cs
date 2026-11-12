using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.ErasePersonalData;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

/// <summary>The erasure decided in Customer Onboarding, as relayed by KYC's subscriber.</summary>
public sealed record ErasePersonalDataRequest(
    string? CustomerNumber,
    [property: JsonRequired] Guid CausationId,
    Guid? WorkflowId,
    Guid? CorrelationId);

[ApiController]
[Route("internal/v1/kyc/personal-data-erasures")]
public sealed class InternalPersonalDataErasuresController(ErasePersonalDataCommandHandler handler) : ControllerBase
{
    private const int MaxCustomerNumberLength = 100;

    /// <summary>
    /// Erases the applicant from every KYC case of the customer. Idempotent: called again, it
    /// erases nothing new and answers 200 with the same totals.
    /// </summary>
    [HttpPost]
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Internal endpoint: only KYC's own subscriber (pinned client_id) may call it - the same
    // │ client that opens cases from the onboarding events.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [Authorize(Policy = "KycCaseOpeningSubscriberWrite")]
    public async Task<ActionResult<ErasePersonalDataResult>> Erase(
        [FromBody] ErasePersonalDataRequest request,
        CancellationToken cancellationToken)
    {
        var customerNumber = request.CustomerNumber?.Trim();
        if (string.IsNullOrEmpty(customerNumber) || customerNumber.Length > MaxCustomerNumberLength)
            return ValidationProblem("Give the customer number.");

        if (request.CausationId == Guid.Empty)
            return ValidationProblem("Give the erasure event's message ID as CausationId.");

        try
        {
            return Ok(await handler.HandleAsync(
                new ErasePersonalDataCommand(customerNumber, request.CausationId, request.WorkflowId, request.CorrelationId),
                cancellationToken));
        }
        catch (ConcurrencyConflictException)
        {
            // RESIL: an officer changed a case at that moment - 503, so the subscriber tries again.
            return Problem("A case changed while it was being erased; try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}