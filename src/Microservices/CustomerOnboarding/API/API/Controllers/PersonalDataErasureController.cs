using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

/// <summary>The customer to erase and why: a reason CODE (CUSTOMER_REQUEST or RETENTION_ENDED), never free text.</summary>
public sealed record RequestPersonalDataErasureRequest(string? CustomerNumber, string? ReasonCode);

/// <summary>The second person's remarks: required to reject, ignored to approve.</summary>
public sealed record PersonalDataErasureDecisionRequest(string? Remarks);

/// <summary>The retention in force and the law's, in whole days (no personal data).</summary>
public sealed record PersonalDataErasurePolicyResponse(int RetentionDays, int LegalRetentionDays, bool IsShorterThanLegal);

// ┌─ :: SECURITY :: Personal-data erasure endpoints (operations, four eyes) ───────────────────────
// │ Why : Erasure cannot be undone; it must be asked for by one operator and decided by another,
// │       each acting through the Operations app with a recent authenticator code.
// │ How : Policies PersonalDataErasureView / Request / Decide (Program.cs): scope + permission +
// │       delegation by the Operations Journey API + step-up. The acting person is the token's
// │       subject - never a value from the body. Input is a customer number and a reason code.
// │ Ref : doc/Crypto-Shredding.md | OWASP API5 BFLA | RFC 8693 | RFC 9470
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("v1/customers/personal-data-erasures")]
public sealed class PersonalDataErasureController(
    PersonalDataErasureCommandHandler commands,
    GetPersonalDataErasureRequestsQueryHandler queries,
    PersonalDataErasurePolicy policy) : ControllerBase
{
    private const int MaxCustomerNumberLength = 30;

    // ARCH: the API owns the retention setting, so the screen asks it instead of keeping a copy.
    [HttpGet("policy")]
    [Authorize(Policy = "PersonalDataErasureView")]
    public ActionResult<PersonalDataErasurePolicyResponse> Policy() =>
        Ok(new PersonalDataErasurePolicyResponse(
            (int)policy.RetentionPeriod.TotalDays,
            (int)PersonalDataErasurePolicy.LegalRetentionPeriod.TotalDays,
            policy.IsShorterThanLegal));

    [HttpGet]
    [Authorize(Policy = "PersonalDataErasureView")]
    public async Task<ActionResult<IReadOnlyList<PersonalDataErasureRequestDto>>> List(CancellationToken cancellationToken)
    {
        var caller = CustomerResourceAuthorization.GetActingUserId(User);
        if (caller is null)
            return Forbid();

        return Ok(await queries.HandleAsync(caller, cancellationToken));
    }

    [HttpPost]
    [Authorize(Policy = "PersonalDataErasureRequest")]
    public async Task<ActionResult<PersonalDataErasureResult>> RequestErasure(
        [FromBody] RequestPersonalDataErasureRequest request,
        CancellationToken cancellationToken)
    {
        var caller = CustomerResourceAuthorization.GetActingUserId(User);
        if (caller is null)
            return Forbid();

        var customerNumber = request.CustomerNumber?.Trim();
        if (string.IsNullOrEmpty(customerNumber) || customerNumber.Length > MaxCustomerNumberLength)
            return ValidationProblem("Give the customer number.");

        if (!PersonalDataErasureReasonCode.TryFromCode(request.ReasonCode, out var reason))
            return ValidationProblem("ReasonCode must be CUSTOMER_REQUEST or RETENTION_ENDED.");

        var result = await commands.RequestAsync(
            new RequestPersonalDataErasureCommand(customerNumber, reason, caller), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpPost("{id:long}/approve")]
    [Authorize(Policy = "PersonalDataErasureDecide")]
    public Task<ActionResult<PersonalDataErasureResult>> Approve(long id, CancellationToken cancellationToken) =>
        DecideAsync(id, approve: true, remarks: null, cancellationToken);

    [HttpPost("{id:long}/reject")]
    [Authorize(Policy = "PersonalDataErasureDecide")]
    public Task<ActionResult<PersonalDataErasureResult>> Reject(
        long id,
        [FromBody] PersonalDataErasureDecisionRequest? request,
        CancellationToken cancellationToken) =>
        DecideAsync(id, approve: false, request?.Remarks, cancellationToken);

    private async Task<ActionResult<PersonalDataErasureResult>> DecideAsync(
        long id, bool approve, string? remarks, CancellationToken cancellationToken)
    {
        var caller = CustomerResourceAuthorization.GetActingUserId(User);
        if (caller is null)
            return Forbid();

        if (remarks?.Length > PersonalDataErasureRequest.MaxRemarksLength)
            return ValidationProblem($"The remarks may be at most {PersonalDataErasureRequest.MaxRemarksLength} characters.");

        return Ok(await commands.DecideAsync(
            new DecidePersonalDataErasureCommand(id, approve, remarks, caller), cancellationToken));
    }
}