using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.OnlineIdentity;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

/// <summary>
/// The IDP's questions while a customer registers for online access ("My EWP"): do these details
/// belong to an onboarded customer, and - after the one-time code - link the new sign-in to them.
/// Machine-only: called by the IDP itself, with its pinned identity.
/// </summary>
[ApiController]
[Route("internal/v1/customers/online-identity")]
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Only the IDP's own customer-directory identity (client_id pinned) with the one scope
// │ for this purpose - no staff token, no other machine client.
// └──────────────────────────────────────────────────────────────────────────────────────
[Authorize(Policy = "CustomerOnlineIdentity")]
public sealed class InternalCustomerOnlineIdentityController(
    CustomerOnlineIdentityHandler handler,
    ILogger<InternalCustomerOnlineIdentityController> logger) : ControllerBase
{
    /// <summary>The customer these details belong to (name to greet, last digits of the mobile), or 404.</summary>
    [HttpPost("candidates")]
    public async Task<IActionResult> FindCandidate([FromBody] OnlineIdentityCheckRequest request, CancellationToken cancellationToken)
    {
        var candidate = await handler.FindCandidateAsync(request.CustomerNumber, request.Email, cancellationToken);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ No match, not onboarded, already registered: ONE answer (404) - the reason is not
        // │ the caller's business, and the IDP shows the person the same page either way.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (candidate is null)
        {
            logger.LogInformation("Online registration: no eligible customer for the details given.");
            return NotFound();
        }

        return Ok(candidate);
    }

    /// <summary>Links the IDP subject to the customer (after the one-time code): 204, 404 or 409.</summary>
    [HttpPost("links")]
    public async Task<IActionResult> Link([FromBody] OnlineIdentityLinkRequest request, CancellationToken cancellationToken)
    {
        if (request.SubjectId == Guid.Empty)
            return ValidationProblem("SubjectId is required.");

        var outcome = await handler.LinkAsync(request.CustomerNumber, request.Email, request.SubjectId, cancellationToken);
        logger.LogInformation("Online identity link for customer {CustomerNumber}: {Outcome}.", request.CustomerNumber, outcome);

        return outcome switch
        {
            OnlineIdentityOutcome.Linked => NoContent(),
            OnlineIdentityOutcome.AlreadyRegistered => Problem(statusCode: StatusCodes.Status409Conflict, title: "The customer already has an online identity"),
            _ => NotFound()
        };
    }
}

public sealed record OnlineIdentityCheckRequest(string CustomerNumber, string Email);

public sealed record OnlineIdentityLinkRequest(string CustomerNumber, string Email, [property: JsonRequired] Guid SubjectId);