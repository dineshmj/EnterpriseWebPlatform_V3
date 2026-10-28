using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Bian;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: /v2 = BIAN CurrentAccount, next to /v1 ─────────────────────────
// │ Why : Shows the Accounts API speaking BIAN's standard contract (release 14.0.0) to anyone who
// │       expects it, while EWP's own callers keep /v1.
// │ How : Two BIAN operations, each on the SAME queries, command handler and policies as /v1:
// │         GET  v2/CurrentAccount/{id}/Retrieve          (AccountView: an account officer,
// │                                                         own branch - as GET /v1/accounts/{id})
// │         POST v2/CurrentAccount/{id}/Deposit/Initiate  (CashDepositRecord: the counter, i.e. the
// │                                                         Payments BFF's client only, own branch,
// │                                                         Idempotency-Key - as /v1 cash deposits)
// │       NOT exposed: BIAN's AmountBlock (hold, settle, release funds). In EWP those are the
// │       payment saga's internal, machine-only commands (one pinned client, Inbox, replies via
// │       the Outbox); a second HTTP door into moving money would add risk and show nothing new.
// │       The holds are visible: current balance - available balance (Retrieve).
// │ Ref : doc/bian/README.md | BIAN CurrentAccount.yaml (release 14.0.0) | OWASP API1, API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("v2/CurrentAccount")]
[FeatureGate(FeatureFlagKeys.BianV2Endpoints, defaultValue: true)]   // release toggle: off = 404 (Common/WebUtilities/FeatureFlags)
public sealed class CurrentAccountController(
    IAccountsQueries queries,
    RecordCashDepositCommandHandler depositHandler) : ControllerBase
{
    /// <summary>BIAN Retrieve: the account. Another branch's account does not exist for the caller (as in /v1).</summary>
    [HttpGet("{currentaccountid:long}/Retrieve")]
    [Authorize(Policy = "AccountView")]
    public async Task<IActionResult> Retrieve(long currentaccountid, CancellationToken cancellationToken)
    {
        if (!BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) || branch is null)
            return Forbid();

        var account = await queries.GetAccountAsync(currentaccountid, branch, cancellationToken);
        return account is null ? NotFound() : new JsonResult(CurrentAccountMapper.ToFacility(account), BianJson.Options);
    }

    /// <summary>
    /// BIAN Deposit/Initiate: cash paid in at the counter, recorded and credited at once (201; 200 for a
    /// repeated Idempotency-Key, as in /v1). The account is the path's; the customer must own it.
    /// </summary>
    [HttpPost("{currentaccountid:long}/Deposit/Initiate")]
    [Authorize(Policy = "CashDepositRecord")]
    public async Task<IActionResult> InitiateDeposit(
        long currentaccountid,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] Deposit? request,
        CancellationToken cancellationToken)
    {
        var staff = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(staff))
            return Unauthorized();
        if (!BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) || branch is null)
            return Forbid();
        if (!Guid.TryParse(idempotencyKey, out var depositRef) || depositRef == Guid.Empty)
            return ValidationProblem("An Idempotency-Key header (a GUID) is required.");

        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);
        if (deposit is null)
            return ValidationProblem(string.Join(" ", errors));

        // SEC: the account is looked up in the caller's branch only - another branch's account is the same 404.
        var account = await queries.GetAccountAsync(currentaccountid, branch, cancellationToken);
        if (account is null)
            return NotFound();

        try
        {
            var result = await depositHandler.HandleAsync(
                new RecordCashDepositCommand(depositRef, deposit.CustomerNumber, account.Bsb, account.AccountNumber, deposit.Amount, branch, staff),
                cancellationToken);
            if (result is null)
                return NotFound();

            return new JsonResult(CurrentAccountMapper.ToDeposit(result), BianJson.Options)
            {
                StatusCode = result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK
            };
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Idempotency-Key conflict");
        }
    }
}