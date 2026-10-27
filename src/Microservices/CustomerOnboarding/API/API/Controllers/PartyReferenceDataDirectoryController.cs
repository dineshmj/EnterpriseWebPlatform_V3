using System.Globalization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.API.Bian;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: /v2 = BIAN PartyReferenceDataDirectory, next to /v1 ────────────
// │ Why : Shows the Customer Onboarding API speaking BIAN's standard contract (release 14.0.0) to
// │       anyone who expects it, while EWP's own callers keep /v1.
// │ How : Three BIAN operations, each on the SAME query, command handler, policies and object-level
// │       checks as /v1:
// │         GET v2/PartyReferenceDataDirectory/{id}/Retrieve                    (CustomerRead, in
// │                                                    scope - as GET /v1/customers/{id})
// │         GET v2/PartyReferenceDataDirectory/{id}/Reference/primary/Retrieve  (the same)
// │         PUT v2/PartyReferenceDataDirectory/{id}/Reference/primary/Update    (CustomerWrite, the
// │                                                    managing agent - as PUT /v1/customers/{id})
// │       NOT offered: BIAN's Register. Its body (the directory entry) holds a name, a party type
// │       and an identifier only - no e-mail, phone or address, without which EWP registers no
// │       customer - and BIAN's Reference has no Register of its own.
// │       /v1's ExpectedVersion is HTTP's own concurrency check here: each answer carries the
// │       customer's version as its ETag, and Update needs it in If-Match (428 without, 412 stale).
// │ Ref : doc/bian/README.md | BIAN PartyReferenceDataDirectory.yaml (release 14.0.0) |
// │       OWASP API1, API5 | RFC 9110 13.1.1 (If-Match), RFC 6585 (428)
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("v2/PartyReferenceDataDirectory")]
public sealed class PartyReferenceDataDirectoryController(
    GetCustomerQueryHandler getCustomerHandler,
    UpdateCustomerCommandHandler updateCustomerHandler,
    CustomerResourceAuthorization resourceAuthorization) : ControllerBase
{
    /// <summary>BIAN Retrieve: the directory entry. A customer outside the caller's scope does not exist for them (as in /v1).</summary>
    [HttpGet("{partyreferencedatadirectoryid:long}/Retrieve")]
    [Authorize(Policy = "CustomerRead")]
    public async Task<IActionResult> Retrieve(long partyreferencedatadirectoryid, CancellationToken cancellationToken)
    {
        var customer = await VisibleCustomerAsync(partyreferencedatadirectoryid, cancellationToken);
        return customer is null ? NotFound() : Bian(PartyReferenceDataDirectoryMapper.ToEntry(customer), customer.Version);
    }

    /// <summary>BIAN RetrieveReference: legal name, e-mail, phone and residential address.</summary>
    [HttpGet("{partyreferencedatadirectoryid:long}/Reference/{referenceid}/Retrieve")]
    [Authorize(Policy = "CustomerRead")]
    public async Task<IActionResult> RetrieveReference(long partyreferencedatadirectoryid, string referenceid, CancellationToken cancellationToken)
    {
        if (referenceid != PartyReferenceDataDirectoryMapper.ReferenceId)
            return NotFound();

        var customer = await VisibleCustomerAsync(partyreferencedatadirectoryid, cancellationToken);
        return customer is null ? NotFound() : Bian(PartyReferenceDataDirectoryMapper.ToReference(customer), customer.Version);
    }

    /// <summary>
    /// BIAN UpdateReference: a new e-mail and / or phone number (what is not sent stays). Only the
    /// managing agent, with the version last seen in If-Match. Answers the reference as it is now.
    /// </summary>
    [HttpPut("{partyreferencedatadirectoryid:long}/Reference/{referenceid}/Update")]
    [Authorize(Policy = "CustomerWrite")]
    public async Task<IActionResult> UpdateReference(
        long partyreferencedatadirectoryid,
        string referenceid,
        [FromBody] Reference? request,
        CancellationToken cancellationToken)
    {
        if (referenceid != PartyReferenceDataDirectoryMapper.ReferenceId)
            return NotFound();

        // SEC: ABAC (visible in the branch) + ReBAC (the caller manages this customer) - /v1's check.
        var access = await resourceAuthorization.CanManageCustomerAsync(User, partyreferencedatadirectoryid, cancellationToken);
        if (access == ResourceAccess.NotFound)
            return NotFound();
        if (access == ResourceAccess.Forbidden)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not the managing agent", detail: "Only the customer's managing agent may change the customer.");

        if (!TryReadIfMatch(out var expectedVersion))
            return Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required", detail: "Send the ETag of the last Retrieve in If-Match, so a change made meanwhile is not overwritten.");

        var current = await getCustomerHandler.HandleAsync(new GetCustomerQuery(partyreferencedatadirectoryid), cancellationToken);
        if (current is null)
            return NotFound();

        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, current);
        if (change is null)
            return ValidationProblem(string.Join(" ", errors));

        try
        {
            // The name is passed on unchanged: /v1's command changes name and contact details together.
            await updateCustomerHandler.HandleAsync(
                new UpdateCustomerCommand(partyreferencedatadirectoryid, current.FirstName, current.LastName, change.Email, change.PhoneNumber, expectedVersion),
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Changed meanwhile", detail: "The customer was changed after the ETag in If-Match. Retrieve again and resend.");
        }

        var updated = await getCustomerHandler.HandleAsync(new GetCustomerQuery(partyreferencedatadirectoryid), cancellationToken);
        return updated is null ? NotFound() : Bian(PartyReferenceDataDirectoryMapper.ToReference(updated), updated.Version);
    }

    private async Task<CustomerDetailsDto?> VisibleCustomerAsync(long customerId, CancellationToken cancellationToken) =>
        await resourceAuthorization.CanAccessCustomerAsync(User, customerId, cancellationToken)
            ? await getCustomerHandler.HandleAsync(new GetCustomerQuery(customerId), cancellationToken)
            : null;

    private JsonResult Bian(object body, long version)
    {
        Response.Headers.ETag = ETagOf(version);
        return new JsonResult(body, BianJson.Options);
    }

    private static string ETagOf(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────
    // │ One strong entity tag, exactly as Retrieve sent it. A weak tag (W/"...") or "*" is not a
    // │ version and is refused, so an Update can never skip the concurrency check.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    private bool TryReadIfMatch(out long version)
    {
        version = 0;
        var header = Request.Headers.IfMatch.ToString();
        return EntityTagHeaderValue.TryParse(header, out var tag)
            && !tag.IsWeak
            && long.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }
}