using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

/// <summary>
/// For the assisted payment screen (Payments BFF, with the staff member's own token):
/// find a customer's ACTIVE accounts in the staff member's branch, with the amount
/// available for a new payment. A convenience for the screen only - the reservation in
/// the payment saga is the authoritative check, under the account's row lock.
/// </summary>
[ApiController]
[Route("v1/accounts/for-payment")]
[Authorize(Policy = "PaymentAccountLookup")]
public sealed class PaymentAccountsController(IAccountsQueries queries) : ControllerBase
{
    private const int MinSearchLength = 2;

    /// <summary>Accounts whose holder name or customer number contains <paramref name="search"/> (at most 20).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PaymentAccount>>> Search([FromQuery] string? search, CancellationToken cancellationToken = default)
    {
        if (!BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) || branch is null)
            return Forbid();

        var term = search?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < MinSearchLength || term.Length > 100)
            return ValidationProblem($"Search with {MinSearchLength} to 100 characters of the customer's name or number.");

        return Ok(await queries.FindAccountsForPaymentAsync(branch, term, cancellationToken));
    }
}