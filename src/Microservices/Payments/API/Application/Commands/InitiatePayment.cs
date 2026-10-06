using System.Security.Cryptography;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

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
                payment.PaymentNumber, command.InitiatedByUserId, command.Branch, payment.Amount, from, to, payment.ApprovalRequired, saga.Id);

            return new InitiatePaymentResult(payment.Id, payment.PaymentNumber, payment.Status.ToCode(), Created: true);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same Idempotency-Key arrived twice at the same time: the loser answers with the winner's payment.
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

        return new InitiatePaymentResult(existing.Id, existing.PaymentNumber, existing.Status.ToCode(), Created: false);
    }

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