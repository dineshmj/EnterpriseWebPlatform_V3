using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

public sealed record CashDepositRequest(string? CustomerNumber, string? Bsb, string? AccountNumber, decimal? Amount);

// ┌─ :: SECURITY :: Who may credit an account ─────────────────────────────────────────────────────
// │ Why : Crediting money is the most attractive thing to misuse in a bank: it must come only from a
// │       counter session of a person allowed to take cash, for an account of their own branch.
// │ How : Policy CashDepositRecord - accounts.write + permission account.deposit.record + a branch
// │       claim + the Payments BFF's client ID (the counter screen; no other client, no machine).
// │       The branch, the customer and the account's state are checked again in the aggregate; an
// │       unknown or other-branch account is the same 404.
// │ Ref : Features 2.2.1, 2.2.3 | OWASP API1 BOLA, API5 BFLA | Authorization-Model 4
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Cash deposits at the counter (Payments BFF, with the staff member's own token). The
/// request's Idempotency-Key is the deposit's DepositRef.
/// </summary>
[ApiController]
[Route("v1/accounts/cash-deposits")]
[Authorize(Policy = "CashDepositRecord")]
public sealed class CashDepositsController(RecordCashDepositCommandHandler handler) : ControllerBase
{
    /// <summary>Records the cash and credits the account: 201 when recorded, 200 for a repeated request.</summary>
    [HttpPost]
    public async Task<IActionResult> Record(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CashDepositRequest request,
        CancellationToken cancellationToken = default)
    {
        var staff = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(staff))
            return Unauthorized();
        if (!BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) || branch is null)
            return Forbid();

        if (!Guid.TryParse(idempotencyKey, out var depositRef) || depositRef == Guid.Empty)
            return ValidationProblem("An Idempotency-Key header (a GUID) is required.");
        if (string.IsNullOrWhiteSpace(request.CustomerNumber) || string.IsNullOrWhiteSpace(request.Bsb) ||
            string.IsNullOrWhiteSpace(request.AccountNumber) || request.Amount is null)
        {
            return ValidationProblem("The customer number, BSB, account number and amount are required.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new RecordCashDepositCommand(depositRef, request.CustomerNumber, request.Bsb, request.AccountNumber,
                    request.Amount.Value, branch, staff),
                cancellationToken);

            if (result is null)
                return NotFound(new { error = "No account of your branch has this BSB and account number." });
            return result.Created ? StatusCode(StatusCodes.Status201Created, result) : Ok(result);
        }
        catch (DomainConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}