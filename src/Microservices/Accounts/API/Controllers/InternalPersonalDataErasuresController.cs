using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Application.Commands;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

/// <summary>The erasure decided in Customer Onboarding, as relayed by Accounts' subscriber.</summary>
public sealed record ErasePersonalDataRequest(
    string? CustomerNumber,
    Guid CausationId,
    Guid? WorkflowId,
    Guid? CorrelationId);

[ApiController]
[Route("internal/v1/accounts/personal-data-erasures")]
public sealed class InternalPersonalDataErasuresController(ErasePersonalDataCommandHandler handler) : ControllerBase
{
    private const int MaxCustomerNumberLength = 100;

    /// <summary>
    /// Erases the holder's name from every account application of the customer. Idempotent: called again, it
    /// erases nothing new and answers 200 with the same totals.
    /// </summary>
    [HttpPost]
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Internal endpoint: only Accounts' own subscriber (pinned client_id) may call it - the
    // │ same client that opens applications from the Compliance events.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [Authorize(Policy = "AccountApplicationOpeningSubscriberWrite")]
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
            // RESIL: an officer or the opening worker changed an application - 503, so the subscriber tries again.
            return Problem("An application changed while it was being erased; try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}