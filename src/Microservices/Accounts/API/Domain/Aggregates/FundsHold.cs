using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: the funds one payment reserved on one account, keyed by the payment's
/// never-repeating PaymentRef. Accounts knows nothing about the payment saga: it reserves,
/// debits or releases when asked, and answers every command with a reply event.
///
/// Every command for the same PaymentRef is idempotent: repeating it changes nothing and
/// repeats the reply, so the orchestrator may resend after a timeout without risk.
///
///   HELD ──settle──► SETTLED      (the funds left the account)
///   HELD ──release─► RELEASED     (compensation: the funds are available again)
///   REFUSED                       (nothing was held - nothing to undo)
///   (none) ──release─► RELEASED   (a release that arrived first: a later reservation is refused)
/// </summary>
public sealed class FundsHold : AggregateRoot
{
    private FundsHold()
    {
        PaymentNumber = null!;
        Bsb = null!;
        AccountNumber = null!;
        CustomerNumber = null!;
        Currency = null!;
    }

    public long Id { get; private set; }

    /// <summary>The payment (Payments' identity, by value) and the idempotency key of every command.</summary>
    public Guid PaymentRef { get; private set; }

    public string PaymentNumber { get; private set; }

    /// <summary>The account the funds are held on; null when it was not found (or for a release-first tombstone).</summary>
    public long? AccountId { get; private set; }

    public string Bsb { get; private set; }

    public string AccountNumber { get; private set; }

    public string CustomerNumber { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public FundsHoldStatus Status { get; private set; }

    public FundsRefusalReason? RefusalReason { get; private set; }

    /// <summary>The person who started the payment (copied into the replies' envelope).</summary>
    public string? InitiatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>
    /// Reserves the funds on the account, or records why not. Either way the result is a
    /// hold row (the record of the answer) and a reply event.
    /// </summary>
    public static FundsHold Reserve(
        Guid paymentRef, string paymentNumber, Account account, string customerNumber,
        decimal amount, string currency, string? initiatedByUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        var hold = New(paymentRef, paymentNumber, account.Bsb, account.AccountNumber, customerNumber, amount, currency, initiatedByUserId, now);
        hold.AccountId = account.Id;

        if (account.RefusalToHold(hold.Amount, hold.Currency, hold.CustomerNumber) is { } refusal)
        {
            hold.Refuse(refusal, now);
            return hold;
        }

        account.Hold(hold.Amount, now);
        hold.Status = FundsHoldStatus.Held;
        hold.RaiseDomainEvent(new FundsReservedDomainEvent(now));
        return hold;
    }

    /// <summary>The paying account does not exist: refused, nothing held.</summary>
    public static FundsHold RefuseUnknownAccount(
        Guid paymentRef, string paymentNumber, string bsb, string accountNumber, string customerNumber,
        decimal amount, string currency, string? initiatedByUserId, DateTimeOffset now)
    {
        var hold = New(paymentRef, paymentNumber, bsb, accountNumber, customerNumber, amount, currency, initiatedByUserId, now);
        hold.Refuse(FundsRefusalReason.AccountNotFound, now);
        return hold;
    }

    /// <summary>
    /// A release for a payment Accounts has never seen (its reservation was lost or is still
    /// on its way). Recorded as RELEASED, so that a late reservation is refused instead of
    /// holding funds nobody will ever release.
    /// </summary>
    public static FundsHold ReleaseBeforeReservation(
        Guid paymentRef, string paymentNumber, string bsb, string accountNumber, string customerNumber,
        string currency, string? initiatedByUserId, DateTimeOffset now)
    {
        var hold = New(paymentRef, paymentNumber, bsb, accountNumber, customerNumber, 0m, currency, initiatedByUserId, now, validateAmount: false);
        hold.Status = FundsHoldStatus.Released;
        hold.ReleasedAt = now;
        hold.RaiseDomainEvent(new FundsReleasedDomainEvent(NothingWasHeld: true, now));
        return hold;
    }

    /// <summary>A reservation command for a payment already answered: repeat the answer, change nothing.</summary>
    public void RepeatReservation(DateTimeOffset now)
    {
        switch (Status)
        {
            case FundsHoldStatus.Held:
            case FundsHoldStatus.Settled:
                RaiseDomainEvent(new FundsReservedDomainEvent(now));
                break;
            case FundsHoldStatus.Refused:
                RaiseDomainEvent(new FundsReservationRefusedDomainEvent(RefusalReason!.Value, now));
                break;
            case FundsHoldStatus.Released:
                RaiseDomainEvent(new FundsReservationRefusedDomainEvent(FundsRefusalReason.AlreadyReleased, now));
                break;
        }
    }

    /// <summary>The payment was sent: the held funds are debited.</summary>
    public void Settle(Account account, DateTimeOffset now)
    {
        switch (Status)
        {
            case FundsHoldStatus.Settled:
                RaiseDomainEvent(new FundsSettledDomainEvent(now));   // repeated command
                return;
            case FundsHoldStatus.Held:
                EnsureSameAccount(account);
                account.DebitHeld(Amount, now);
                Status = FundsHoldStatus.Settled;
                SettledAt = now;
                Touch(now);
                RaiseDomainEvent(new FundsSettledDomainEvent(now));
                return;
            default:
                throw new DomainConflictException($"Funds for payment {PaymentNumber} are {Status.ToCode()}: there is nothing to settle.");
        }
    }

    /// <summary>
    /// Compensation: the reserved funds become available again. Releasing what was never
    /// held (refused) or is already released is a harmless repeat. Funds that already left
    /// the account cannot be released.
    /// </summary>
    public void Release(Account? account, DateTimeOffset now)
    {
        switch (Status)
        {
            case FundsHoldStatus.Released:
                RaiseDomainEvent(new FundsReleasedDomainEvent(NothingWasHeld: Amount == 0m || AccountId is null, now));
                return;
            case FundsHoldStatus.Refused:
                RaiseDomainEvent(new FundsReleasedDomainEvent(NothingWasHeld: true, now));
                return;
            case FundsHoldStatus.Held:
                ArgumentNullException.ThrowIfNull(account);
                EnsureSameAccount(account);
                account.ReleaseHeld(Amount, now);
                Status = FundsHoldStatus.Released;
                ReleasedAt = now;
                Touch(now);
                RaiseDomainEvent(new FundsReleasedDomainEvent(NothingWasHeld: false, now));
                return;
            default:
                throw new DomainConflictException($"Funds for payment {PaymentNumber} were already debited: they cannot be released.");
        }
    }

    private void Refuse(FundsRefusalReason reason, DateTimeOffset now)
    {
        Status = FundsHoldStatus.Refused;
        RefusalReason = reason;
        RaiseDomainEvent(new FundsReservationRefusedDomainEvent(reason, now));
    }

    private void EnsureSameAccount(Account account)
    {
        if (AccountId != account.Id)
            throw new DomainConflictException("The funds hold belongs to a different account.");
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static FundsHold New(
        Guid paymentRef, string paymentNumber, string bsb, string accountNumber, string customerNumber,
        decimal amount, string currency, string? initiatedByUserId, DateTimeOffset now, bool validateAmount = true)
    {
        if (paymentRef == Guid.Empty)
            throw new DomainRuleViolationException("A PaymentRef is required.");
        if (string.IsNullOrWhiteSpace(paymentNumber) || string.IsNullOrWhiteSpace(bsb) ||
            string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(customerNumber))
        {
            throw new DomainRuleViolationException("The payment number, BSB, account number and customer number are required.");
        }

        return new FundsHold
        {
            PaymentRef = paymentRef,
            PaymentNumber = paymentNumber.Trim(),
            Bsb = bsb.Trim(),
            AccountNumber = accountNumber.Trim(),
            CustomerNumber = customerNumber.Trim(),
            Amount = validateAmount ? Money.ValidAmount(amount) : amount,
            Currency = Money.ValidCurrency(currency),
            InitiatedByUserId = string.IsNullOrWhiteSpace(initiatedByUserId) ? null : initiatedByUserId.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }
}