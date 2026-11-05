using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.ErasePersonalData;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Controllers;

/// <summary>The erasure decided in Customer Onboarding, as relayed by Documents Management's subscriber.</summary>
public sealed record ErasePersonalDataRequest(
    string? CustomerNumber,
    Guid CausationId,
    Guid? WorkflowId,
    Guid? CorrelationId);

[ApiController]
[Route("internal/v1/documents/personal-data-erasures")]
public sealed class InternalPersonalDataErasuresController(ErasePersonalDataCommandHandler handler) : ControllerBase
{
    private const int MaxCustomerNumberLength = 100;

    /// <summary>
    /// Blanks the file name and deletes the content of every document filed under the customer.
    /// Idempotent: called again, it erases nothing new and answers 200 with the same totals.
    /// </summary>
    [HttpPost]
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Internal endpoint: only Documents Management's own subscriber (pinned client_id) may
    // │ call it - the same client that invalidates documents of rejected applications.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [Authorize(Policy = "DocumentInvalidationSubscriberWrite")]
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
            return Ok(await handler.HandleAsync(new ErasePersonalDataCommand(customerNumber, request.CausationId), cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            // RESIL: a document changed at that moment - 503, so the subscriber tries again.
            return Problem("A document changed while it was being erased; try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}