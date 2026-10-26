using System.Security.Cryptography;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

/// <summary>
/// A staff member records cash a customer paid in at the counter. DepositRef is the request's
/// Idempotency-Key: pressing Deposit twice, or a resend after a lost answer, finds the same
/// deposit instead of crediting the account twice.
/// </summary>
public sealed record RecordCashDepositCommand(
    Guid DepositRef,
    string CustomerNumber,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    BranchCode Branch,
    string ReceivedByUserId);

public sealed record CashDepositResult(
    string DepositNumber,
    string CustomerNumber,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency,
    decimal BalanceAfter,
    DateTimeOffset RecordedAt,
    bool Created);

// ┌─ :: ARCHITECTURE + SECURITY :: One credit per Idempotency-Key, in one transaction ─────────────
// │ Why : Money credited twice is as wrong as money lost; a retried POST must find the first deposit.
// │ How : The account row is locked, the deposit recorded and the account credited, and the event
// │       written to the Outbox - all in ONE transaction. The same key returns the deposit already
// │       recorded, but only to the same person with the same details (409 otherwise).
// │ Ref : Features 1.4.3, 1.3.1 | IETF draft "The Idempotency-Key HTTP Header" | OWASP API6
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Records a cash deposit (CashDeposit) and credits the account in ONE local transaction,
/// with the CashDeposited event in the Outbox. Returns null when the account does not exist
/// or is held at another branch (the caller answers 404 either way, so another branch's
/// accounts cannot be probed).
/// </summary>
public sealed class RecordCashDepositCommandHandler(
    IAccountRepository accounts,
    ICashDepositRepository deposits,
    IAccountsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<RecordCashDepositCommandHandler> logger)
{
    public async Task<CashDepositResult?> HandleAsync(RecordCashDepositCommand command, CancellationToken cancellationToken)
    {
        if (await deposits.GetByRefAsync(command.DepositRef, cancellationToken) is { } existing)
            return Repeat(existing, command);

        try
        {
            return await RecordAsync(command, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ The same Idempotency-Key arrived twice at the same time: the loser answers with the
            // │ winner's deposit (its own credit was rolled back with its transaction).
            // └──────────────────────────────────────────────────────────────────────────────────────
            unitOfWork.DiscardChanges();
            if (await deposits.GetByRefAsync(command.DepositRef, cancellationToken) is { } winner)
                return Repeat(winner, command);
            throw;
        }
    }

    private async Task<CashDepositResult?> RecordAsync(RecordCashDepositCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the account does not exist or belongs to another branch, answer "not found" - the
        // │ same answer for both, so a counter cannot learn which account numbers exist elsewhere.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var account = await accounts.GetForUpdateAsync(command.Bsb, command.AccountNumber, cancellationToken);
        if (account is null || !account.IsInBranch(command.Branch))
            return null;

        var deposit = CashDeposit.Record(
            command.DepositRef, NewDepositNumber(now), account, command.CustomerNumber, command.Amount,
            command.Branch, command.ReceivedByUserId, now);
        deposits.Add(deposit);

        // A deposit is a workflow of its own: its key names the workflow and causes the event.
        await unitOfWork.SaveChangesAsync(new WorkflowContext(command.DepositRef, command.DepositRef, command.DepositRef), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Cash deposit {DepositNumber} recorded by {UserId} (branch {Branch}): {Amount} {Currency} to {Account}.",
            deposit.DepositNumber, command.ReceivedByUserId, command.Branch, deposit.Amount, deposit.Currency,
            LogMasking.Account(deposit.Bsb, deposit.AccountNumber));

        return ToResult(deposit, created: true);
    }

    private static CashDepositResult Repeat(CashDeposit existing, RecordCashDepositCommand command)
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the same key comes from another person or with other details (account, amount),
        // │ refuse with 409 - a reused key never hides a different deposit.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!existing.IsSameRequest(command.Bsb, command.AccountNumber, command.CustomerNumber, command.Amount, command.ReceivedByUserId))
            throw new DomainConflictException("This Idempotency-Key was already used for another deposit. Start a new deposit instead.");

        return ToResult(existing, created: false);
    }

    private static CashDepositResult ToResult(CashDeposit deposit, bool created) =>
        new(deposit.DepositNumber, deposit.CustomerNumber, deposit.Bsb, deposit.AccountNumber, deposit.Amount,
            deposit.Currency, deposit.BalanceAfter, deposit.RecordedAt, created);

    /// <summary>DEP-yyMMdd-XXXXXX: date plus 6 random characters (no 0/O/1/I to avoid misreading).</summary>
    private static string NewDepositNumber(DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"DEP-{now:yyMMdd}-{new string(code)}";
    }
}