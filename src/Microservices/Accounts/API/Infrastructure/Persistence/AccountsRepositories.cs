using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;

public sealed class AccountApplicationRepository(AccountsDbContext db, AccountApplicationPersonalData personalData) : IAccountApplicationRepository
{
    public async Task<AccountApplication?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        await RevealAsync(
            await db.AccountApplications.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken),
            cancellationToken);

    public async Task<AccountApplication?> GetForUpdateAsync(long applicationId, CancellationToken cancellationToken)
    {
        // Lock the row first (concurrent officers queue here; the version column is the
        // second line of defence), then load the aggregate with a normal query.
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM account_applications WHERE id = {applicationId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return locked.Count == 0
            ? null
            : await RevealAsync(await db.AccountApplications.SingleAsync(x => x.Id == applicationId, cancellationToken), cancellationToken);
    }

    public async Task<AccountApplication?> GetNextDueForOpeningAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SKIP LOCKED: several API instances open accounts in parallel without ever taking the same application.
        var ids = await db.Database
            .SqlQuery<long>($"""
                SELECT id AS "Value"
                FROM account_applications
                WHERE status = 'OPENING' AND next_opening_at <= {now} AND personal_data_erased_at IS NULL
                ORDER BY next_opening_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return ids.Count == 0
            ? null
            : await RevealAsync(await db.AccountApplications.SingleAsync(x => x.Id == ids[0], cancellationToken), cancellationToken);
    }

    // Not decrypted: the only caller erases them.
    public async Task<IReadOnlyList<AccountApplication>> ListForErasureAsync(string customerNumber, CancellationToken cancellationToken) =>
        await db.AccountApplications.Where(x => x.CustomerNumber == customerNumber).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public void Add(AccountApplication application) => db.AccountApplications.Add(application);

    private async Task<AccountApplication?> RevealAsync(AccountApplication? application, CancellationToken cancellationToken)
    {
        if (application is not null)
            await personalData.RevealAsync(application, cancellationToken);
        return application;
    }
}

public sealed class AccountRepository(AccountsDbContext db) : IAccountRepository
{
    public void Add(Account account) => db.Accounts.Add(account);

    public async Task<Account?> GetForUpdateAsync(string bsb, string accountNumber, CancellationToken cancellationToken)
    {
        var b = bsb.Trim();
        var n = accountNumber.Trim();
        var ids = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM accounts WHERE bsb = {b} AND account_number = {n} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await db.Accounts.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public async Task<Account?> GetForUpdateAsync(long accountId, CancellationToken cancellationToken)
    {
        var ids = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM accounts WHERE id = {accountId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await db.Accounts.SingleAsync(x => x.Id == accountId, cancellationToken);
    }
}

public sealed class FundsHoldRepository(AccountsDbContext db) : IFundsHoldRepository
{
    public async Task<FundsHold?> GetForUpdateAsync(Guid paymentRef, CancellationToken cancellationToken)
    {
        // One payment's commands are serialised here (a resent command waits for the first).
        var ids = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM funds_holds WHERE payment_ref = {paymentRef} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await db.FundsHolds.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public void Add(FundsHold hold) => db.FundsHolds.Add(hold);
}

public sealed class CashDepositRepository(AccountsDbContext db) : ICashDepositRepository
{
    public Task<CashDeposit?> GetByRefAsync(Guid depositRef, CancellationToken cancellationToken) =>
        db.CashDeposits.AsNoTracking().SingleOrDefaultAsync(x => x.DepositRef == depositRef, cancellationToken);

    public void Add(CashDeposit deposit) => db.CashDeposits.Add(deposit);
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ An application's holder is stored encrypted: the application queries read their page with
// │ the ciphertext, then decrypt only those rows. An erased one reads "Personal data erased".
// └──────────────────────────────────────────────────────────────────────────────────────
public sealed class AccountsQueries(AccountsDbContext db, AccountApplicationPersonalData personalData) : IAccountsQueries
{
    public async Task<PagedResponse<AccountApplicationDetail>> GetApplicationsAsync(
        BranchCode branch, int pageNumber, int pageSize, AccountApplicationStatus? status, CancellationToken cancellationToken)
    {
        var query = db.AccountApplications.AsNoTracking().Where(x => x.BranchCode == branch);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        // ARCH: a status tab is a work queue, so oldest first (first in, first decided). "All" (no status)
        //       is a lookup list, so newest first - the application just received is on page 1.
        var ordered = status is null
            ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

        var total = await query.CountAsync(cancellationToken);
        var page = await ProjectApplications(ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AccountApplicationDetail>(await ShowAsync(page, cancellationToken), pageNumber, pageSize, total);
    }

    public async Task<AccountApplicationDetail?> GetApplicationAsync(long applicationId, BranchCode branch, CancellationToken cancellationToken)
    {
        var row = await ProjectApplications(db.AccountApplications.AsNoTracking().Where(x => x.BranchCode == branch && x.Id == applicationId))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await ShowAsync([row], cancellationToken))[0];
    }

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

    public async Task<IReadOnlyList<PaymentAccount>> FindAccountsForPaymentAsync(BranchCode branch, string term, CancellationToken cancellationToken)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ A LIKE pattern from user input: escape its wildcards so they match literally.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var pattern = "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

        return await db.Accounts.AsNoTracking()
            .Where(x => x.BranchCode == branch && x.Status == AccountStatus.Active)
            .Where(x => EF.Functions.ILike(x.CustomerNumber, pattern) ||
                        EF.Functions.ILike(x.HolderName.FirstName + " " + x.HolderName.LastName, pattern))
            .OrderBy(x => x.HolderName.LastName).ThenBy(x => x.HolderName.FirstName).ThenBy(x => x.AccountNumber)
            .Take(20)
            .Select(x => new PaymentAccount(
                x.CustomerNumber,
                x.HolderName.FirstName + " " + x.HolderName.LastName,
                x.Bsb,
                x.AccountNumber,
                x.Product.ToCode(),
                x.Currency,
                x.Balance - x.HeldAmount))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AccountApplicationDetail>> ShowAsync(IReadOnlyList<ApplicationRow> rows, CancellationToken cancellationToken)
    {
        var names = await personalData.ShowAsync(
            rows.Select(x => (x.Detail.CustomerNumber, x.HolderName, x.Detail.PersonalDataErased)).ToList(),
            cancellationToken);
        return rows.Select((x, i) => x.Detail with { CustomerName = names[i] }).ToList();
    }

    private IQueryable<ApplicationRow> ProjectApplications(IQueryable<AccountApplication> query) =>
        query.Select(x => new ApplicationRow(EF.Property<string>(x, AccountApplicationPersonalData.HolderNameColumn), new AccountApplicationDetail(
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
            string.Empty,   // decrypted after the query
            new StaffLanIds(
                db.StaffMembers.Where(s => s.UserId == x.InitiatedByUserId).Select(s => s.LanId).FirstOrDefault(),
                db.StaffMembers.Where(s => s.UserId == x.ComplianceApprovedByUserId).Select(s => s.LanId).FirstOrDefault(),
                db.StaffMembers.Where(s => s.UserId == x.AssignedOfficerUserId).Select(s => s.LanId).FirstOrDefault(),
                db.StaffMembers.Where(s => s.UserId == x.DecisionByUserId).Select(s => s.LanId).FirstOrDefault()),
            x.PersonalDataErasedAt != null)));

    private sealed record ApplicationRow(string HolderName, AccountApplicationDetail Detail);

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
            x.HolderName.FirstName + " " + x.HolderName.LastName,
            x.Currency,
            x.Balance,
            x.HeldAmount,
            x.Balance - x.HeldAmount));
}