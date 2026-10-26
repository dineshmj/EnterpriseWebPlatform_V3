using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;

// ┌─ :: ARCHITECTURE + SECURITY :: Cash paid in at the counter, credited once ─────────────────────
// │ Why : The balance must change only by money the bank really received - and a double-click or a
// │       resend after a lost answer must never credit the same notes twice.
// │ How : One deposit per DepositRef (the screen's Idempotency-Key, unique); recorded only on an
// │       ACTIVE AUD account of the customer, in the staff member's own branch, and only up to
// │       MaxAmount; the account is credited in the same transaction, under its row lock.
// │ Ref : Features 1.4.3, 1.6.4 | Idempotent Receiver pattern | OWASP API1 BOLA, API6 |
// │       AUSTRAC threshold transaction reports (cash of AUD 10,000 or more)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Aggregate root: cash a customer paid in at the counter, counted and recorded by the staff
/// member who received it, against one of the customer's accounts. Recording it credits the
/// account at once (cash needs no clearing). Never changed afterwards: a mistake would be
/// corrected by a new, opposite entry - not by editing this one.
/// </summary>
public sealed class CashDeposit : AggregateRoot
{
    /// <summary>
    /// The most one deposit may be: below the AUD 10,000 at which a bank must report cash to
    /// AUSTRAC (threshold transaction report), which this PoC does not do.
    /// </summary>
    public const decimal MaxAmount = 9_999.99m;

    private CashDeposit()
    {
        DepositNumber = null!;
        Bsb = null!;
        AccountNumber = null!;
        CustomerNumber = null!;
        BranchCode = null!;
        Currency = null!;
        ReceivedByUserId = null!;
    }

    public long Id { get; private set; }

    /// <summary>The screen's Idempotency-Key: one deposit per key, however often it is sent.</summary>
    public Guid DepositRef { get; private set; }

    /// <summary>The business number people quote (e.g. DEP-261026-7K3QZ2).</summary>
    public string DepositNumber { get; private set; }

    public long AccountId { get; private set; }

    public string Bsb { get; private set; }

    public string AccountNumber { get; private set; }

    public string CustomerNumber { get; private set; }

    /// <summary>The branch whose counter received the cash (always the account's branch).</summary>
    public BranchCode BranchCode { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The account's ledger balance right after this deposit (the customer's receipt).</summary>
    public decimal BalanceAfter { get; private set; }

    /// <summary>The staff member who counted the notes and recorded them.</summary>
    public string ReceivedByUserId { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// Records the cash and credits the account. The account must be locked by the caller (it
    /// is changed here). Refused - nothing recorded, nothing credited - unless the account is
    /// in the staff member's branch, belongs to the customer, is ACTIVE and held in AUD, and
    /// the amount is positive, in cents and at most <see cref="MaxAmount"/>.
    /// </summary>
    public static CashDeposit Record(
        Guid depositRef, string depositNumber, Account account, string customerNumber, decimal amount,
        BranchCode? staffBranch, string receivedByUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (depositRef == Guid.Empty)
            throw new DomainRuleViolationException("A DepositRef (the Idempotency-Key) is required.");
        if (string.IsNullOrWhiteSpace(depositNumber) || string.IsNullOrWhiteSpace(receivedByUserId))
            throw new DomainRuleViolationException("The deposit number and the receiving staff member are required.");
        if (amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            throw new DomainRuleViolationException($"A cash deposit must be more than 0 and at most {MaxAmount:N2} AUD, in whole cents.");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the account is not in the staff member's branch, refuse first - the counter of one
        // │ branch never touches another branch's customer, whatever account number it is given.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!account.IsInBranch(staffBranch))
            throw new DomainConflictException("The account is held at another branch.");
        if (!string.Equals(account.CustomerNumber, customerNumber?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DomainConflictException("The account does not belong to this customer.");
        if (account.Status != AccountStatus.Active)
            throw new DomainConflictException("Cash can be deposited only to an ACTIVE account.");
        if (!string.Equals(account.Currency, Money.Aud, StringComparison.Ordinal))
            throw new DomainConflictException("Cash is accepted in AUD only.");

        account.CreditCash(amount, now);

        var deposit = new CashDeposit
        {
            DepositRef = depositRef,
            DepositNumber = depositNumber.Trim(),
            AccountId = account.Id,
            Bsb = account.Bsb,
            AccountNumber = account.AccountNumber,
            CustomerNumber = account.CustomerNumber,
            BranchCode = account.BranchCode,
            Amount = amount,
            Currency = account.Currency,
            BalanceAfter = account.Balance,
            ReceivedByUserId = receivedByUserId.Trim(),
            RecordedAt = now
        };
        deposit.RaiseDomainEvent(new CashDepositedDomainEvent(now));
        return deposit;
    }

    /// <summary>
    /// True when a repeated request (same Idempotency-Key) asks for exactly this deposit; any
    /// other details under the same key are a different request, not a retry.
    /// </summary>
    public bool IsSameRequest(string bsb, string accountNumber, string customerNumber, decimal amount, string receivedByUserId) =>
        string.Equals(Bsb, bsb?.Trim(), StringComparison.Ordinal) &&
        string.Equals(AccountNumber, accountNumber?.Trim(), StringComparison.Ordinal) &&
        string.Equals(CustomerNumber, customerNumber?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        Amount == amount &&
        string.Equals(ReceivedByUserId, receivedByUserId, StringComparison.Ordinal);
}