using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;

public sealed class AccountApplicationRepository(AccountsDbContext db) : IAccountApplicationRepository
{
    public Task<AccountApplication?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        db.AccountApplications.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken);

    public async Task<AccountApplication?> GetForUpdateAsync(long applicationId, CancellationToken cancellationToken)
    {
        // Lock the row first (concurrent officers queue here; the version column is the
        // second line of defence), then load the aggregate with a normal query.
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM account_applications WHERE id = {applicationId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return locked.Count == 0 ? null : await db.AccountApplications.SingleAsync(x => x.Id == applicationId, cancellationToken);
    }

    public async Task<AccountApplication?> GetNextDueForOpeningAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SKIP LOCKED: several API instances open accounts in parallel without ever taking the same application.
        var ids = await db.Database
            .SqlQuery<long>($"""
                SELECT id AS "Value"
                FROM account_applications
                WHERE status = 'OPENING' AND next_opening_at <= {now}
                ORDER BY next_opening_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : await db.AccountApplications.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public void Add(AccountApplication application) => db.AccountApplications.Add(application);
}

public sealed class AccountRepository(AccountsDbContext db) : IAccountRepository
{
    public void Add(Account account) => db.Accounts.Add(account);
}

public sealed class AccountsQueries(AccountsDbContext db) : IAccountsQueries
{
    public async Task<PagedResponse<AccountApplicationDetail>> GetApplicationsAsync(
        BranchCode branch, int pageNumber, int pageSize, AccountApplicationStatus? status, CancellationToken cancellationToken)
    {
        var query = db.AccountApplications.AsNoTracking().Where(x => x.BranchCode == branch);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        var total = await query.CountAsync(cancellationToken);
        var items = await ProjectApplications(query.OrderBy(x => x.CreatedAt).Skip((pageNumber - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AccountApplicationDetail>(items, pageNumber, pageSize, total);
    }

    public Task<AccountApplicationDetail?> GetApplicationAsync(long applicationId, BranchCode branch, CancellationToken cancellationToken) =>
        ProjectApplications(db.AccountApplications.AsNoTracking().Where(x => x.BranchCode == branch && x.Id == applicationId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResponse<AccountDetail>> GetAccountsAsync(BranchCode branch, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Accounts.AsNoTracking().Where(x => x.BranchCode == branch);
        var total = await query.CountAsync(cancellationToken);
        var items = await ProjectAccounts(query.OrderByDescending(x => x.OpenedAt).Skip((pageNumber - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AccountDetail>(items, pageNumber, pageSize, total);
    }

    public Task<AccountDetail?> GetAccountAsync(long accountId, BranchCode branch, CancellationToken cancellationToken) =>
        ProjectAccounts(db.Accounts.AsNoTracking().Where(x => x.BranchCode == branch && x.Id == accountId))
            .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<AccountApplicationDetail> ProjectApplications(IQueryable<AccountApplication> query) =>
        query.Select(x => new AccountApplicationDetail(
            x.Id,
            x.ApplicationNumber,
            x.CustomerNumber,
            x.ComplianceCaseId,
            x.BranchCode.Value,
            x.Status.ToCode(),
            x.Product == null ? null : x.Product.Value.ToCode(),
            x.InitiatedByUserId,
            x.ComplianceApprovedByUserId,
            x.AssignedOfficerUserId,
            x.HoldReason,
            x.DecisionByUserId,
            x.DecisionAt,
            x.DecisionRemarks,
            x.OpeningAttempts,
            x.NextOpeningAt,
            x.LastOpeningError,
            x.AccountNumber,
            x.OpenedAt,
            x.FailureReason,
            x.CreatedAt,
            x.UpdatedAt,
            x.HolderName.FirstName + " " + x.HolderName.LastName));

    private static IQueryable<AccountDetail> ProjectAccounts(IQueryable<Account> query) =>
        query.Select(x => new AccountDetail(
            x.Id,
            x.AccountNumber,
            x.Bsb,
            x.CustomerNumber,
            x.BranchCode.Value,
            x.Product.ToCode(),
            x.Status.ToCode(),
            x.CoreBankingReference,
            x.OpenedAt,
            x.HolderName.FirstName + " " + x.HolderName.LastName));
}