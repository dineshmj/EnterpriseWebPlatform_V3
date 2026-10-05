using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

/// <summary>Opened accounts of the officer's branch (read-only; ABAC branch scope).</summary>
[ApiController]
[Route("v1/accounts")]
[Authorize(Policy = "AccountView")]
public sealed class AccountsController(IAccountsQueries queries) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AccountDetail>>> GetAccounts(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        return Ok(await queries.GetAccountsAsync(branch, Math.Clamp(pageNumber, 1, 1000), Math.Clamp(pageSize, 1, 100), cancellationToken));
    }

    [HttpGet("{accountId:long}")]
    public async Task<ActionResult<AccountDetail>> GetAccount(long accountId, CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        var item = await queries.GetAccountAsync(accountId, branch, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    private BranchCode? OfficerBranch() =>
        BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) ? branch : null;
}