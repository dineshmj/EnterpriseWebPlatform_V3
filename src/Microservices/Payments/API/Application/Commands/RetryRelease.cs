using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

public sealed record RetryReleaseCommand(long PaymentId, string OperatorLabel);

public sealed record RetryReleaseResult(string Status, string SagaStep);

/// <summary>
/// Operations recovery for a COMPENSATION_FAILED payment: the saga sends ReleaseFunds again
/// with fresh attempts (saga, payment and the command saved in ONE transaction). The saga
/// still decides - it refuses unless the compensation is actually stuck.
/// </summary>
public sealed class RetryReleaseCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    SagaPolicy sagaPolicy,
    TimeProvider clock,
    ILogger<RetryReleaseCommandHandler> logger)
{
    /// <summary>Null when the payment does not exist.</summary>
    public async Task<RetryReleaseResult?> HandleAsync(RetryReleaseCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var paymentRef = await payments.GetRefInBranchAsync(command.PaymentId, branch: null, cancellationToken);
        if (paymentRef is null)
            return null;

        var saga = await sagas.GetForUpdateAsync(paymentRef.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {command.PaymentId} has no saga.");
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);

        saga.RetryCompensation(payment, command.OperatorLabel, sagaPolicy, clock.GetUtcNow());

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Operations ({Operator}) retried the release of the reserved funds for payment {PaymentNumber}.",
            command.OperatorLabel, payment.PaymentNumber);
        return new RetryReleaseResult(payment.Status.ToCode(), saga.Step.ToCode());
    }
}