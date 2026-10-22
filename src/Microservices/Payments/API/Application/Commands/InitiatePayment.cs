using System.Security.Cryptography;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

/// <summary>The approval tier (configuration Payments:ApprovalThreshold): above it, a payments officer approves.</summary>
public sealed record ApprovalTier(decimal Threshold);

/// <summary>
/// A staff member captures a payment for a customer (assisted channel). The PaymentRef is
/// the request's Idempotency-Key: pressing Transfer twice, or a retried request, finds
/// the same payment instead of creating a second one.
/// </summary>
public sealed record InitiatePaymentCommand(
    Guid PaymentRef,
    string CustomerNumber,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    decimal Amount,
    string? Reference,
    BranchCode Branch,
    string InitiatedByUserId);

public sealed record InitiatePaymentResult(long PaymentId, string PaymentNumber, string Status, bool Created);

// ┌─ :: ARCHITECTURE + SECURITY :: One payment per Idempotency-Key ────────────────────────────────
// │ Why : A double-click, a retried POST or a lost answer must never create a second payment.
// │ How : The payment, its saga and the first command are saved in ONE transaction and the request
// │       answers 202 at once; the same key returns the payment already started - but only to the
// │       same person with the same details (409 otherwise).
// │ Ref : Features 1.4.3 | IETF draft "The Idempotency-Key HTTP Header" | AI review 1 fix R5 |
// │       OWASP API6 (business flows)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Records the payment and starts its saga in ONE transaction - payment, saga state and the
/// first command (ReserveFunds) in the Outbox - then returns at once (the API answers 202).
/// Everything after that happens in the background: nothing waits in memory.
/// </summary>
public sealed class InitiatePaymentCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    ApprovalTier approvalTier,
    SagaPolicy sagaPolicy,
    TimeProvider clock,
    ILogger<InitiatePaymentCommandHandler> logger)
{
    public async Task<InitiatePaymentResult> HandleAsync(InitiatePaymentCommand command, CancellationToken cancellationToken)
    {
        var existing = await payments.GetByRefAsync(command.PaymentRef, cancellationToken);
        if (existing is not null)
            return Repeat(existing, command);

        // Validated before anything is written: a bad request never starts a saga.
        var from = BankAccountRef.Create(command.FromBsb, command.FromAccountNumber, "paying");
        var to = BankAccountRef.Create(command.ToBsb, command.ToAccountNumber, "payee");

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            var now = clock.GetUtcNow();

            var payment = Payment.Initiate(
                command.PaymentRef, NewPaymentNumber(now), command.CustomerNumber, from, command.PayeeName, to,
                command.Amount, command.Reference, command.Branch, command.InitiatedByUserId, approvalTier.Threshold, now);
            payments.Add(payment);
            await unitOfWork.SaveAsync(cancellationToken);   // the payment gets its ID

            var saga = PaymentSaga.Start(payment, sagaPolicy, requestId: command.PaymentRef, now);
            sagas.Add(saga);
            await unitOfWork.SaveAsync(cancellationToken);   // saga + ReserveFunds in the Outbox
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Payment {PaymentNumber} initiated by {UserId} (branch {Branch}): {Amount} AUD {From} -> {To}; approval required: {ApprovalRequired}. Saga {SagaId} started.",
                payment.PaymentNumber, command.InitiatedByUserId, command.Branch, payment.Amount,
                LogMasking.Account(from.Bsb, from.AccountNumber), LogMasking.Account(to.Bsb, to.AccountNumber), payment.ApprovalRequired, saga.Id);

            return new InitiatePaymentResult(payment.Id, payment.PaymentNumber, payment.Status.ToCode(), Created: true);
        }
        catch (UniqueConstraintViolationException)
        {
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ The same Idempotency-Key arrived twice at the same time: the loser answers with the winner's payment.
            // └──────────────────────────────────────────────────────────────────────────────────────
            unitOfWork.DiscardChanges();
            var winner = await payments.GetByRefAsync(command.PaymentRef, cancellationToken);
            if (winner is not null)
                return Repeat(winner, command);
            throw;
        }
    }

    private static InitiatePaymentResult Repeat(Payment existing, InitiatePaymentCommand command)
    {
        // An Idempotency-Key belongs to one person's request; reusing someone else's is refused.
        if (!string.Equals(existing.InitiatedByUserId, command.InitiatedByUserId, StringComparison.Ordinal))
            throw new DomainConflictException("This Idempotency-Key was already used for another request.");

        // A retry repeats the SAME payment. The same key with other details is not a retry - it
        // is refused rather than silently answered with the first payment.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the same key comes with other details (amount, payee, accounts), refuse with 409 -
        // │ a reused key never hides a changed payment.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!IsSamePayment(existing, command))
            throw new DomainConflictException("This Idempotency-Key was already used for a payment with other details. Start a new payment instead.");

        return new InitiatePaymentResult(existing.Id, existing.PaymentNumber, existing.Status.ToCode(), Created: false);
    }

    /// <summary>The request's details, normalised exactly as the payment stored them, equal the payment's.</summary>
    private static bool IsSamePayment(Payment existing, InitiatePaymentCommand command) =>
        existing.CustomerNumber == PaymentRules.ValidCustomerNumber(command.CustomerNumber) &&
        existing.From == BankAccountRef.Create(command.FromBsb, command.FromAccountNumber, "paying") &&
        existing.To == BankAccountRef.Create(command.ToBsb, command.ToAccountNumber, "payee") &&
        existing.PayeeName == PaymentRules.ValidPayeeName(command.PayeeName) &&
        existing.Amount == command.Amount &&
        existing.Reference == PaymentRules.ValidReference(command.Reference) &&
        existing.BranchCode == command.Branch;

    /// <summary>PAY-yyMMdd-XXXXXX: date plus 6 random characters (no 0/O/1/I to avoid misreading).</summary>
    private static string NewPaymentNumber(DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"PAY-{now:yyMMdd}-{new string(code)}";
    }
}