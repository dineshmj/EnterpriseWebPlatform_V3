using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

/// <summary>A payments officer's decision on a payment awaiting approval.</summary>
public sealed record DecidePaymentCommand(
    long PaymentId,
    bool Approve,
    string? Remarks,
    string OfficerUserId,
    string OfficerLabel,
    int OfficerClearance,
    BranchCode OfficerBranch);

public sealed record DecidePaymentResult(string Status, string SagaStep);

/// <summary>
/// Hands the officer's decision to the payment's saga, which resumes it: approve → send to
/// the network; reject → release the reserved funds. The rules live in the aggregates
/// (pending only, not the initiator, within the clearance's limit); the branch scope is
/// checked here. Saga, payment and the next command are saved in ONE transaction.
/// </summary>
public sealed class DecidePaymentCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    ApprovalLimits approvalLimits,
    SagaPolicy sagaPolicy,
    TimeProvider clock,
    ILogger<DecidePaymentCommandHandler> logger)
{
    /// <summary>Null when the payment does not exist or belongs to another branch (reported as not found).</summary>
    public async Task<DecidePaymentResult?> HandleAsync(DecidePaymentCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var paymentRef = await payments.GetRefInBranchAsync(command.PaymentId, command.OfficerBranch, cancellationToken);
        if (paymentRef is null)
            return null;

        // The saga's row lock serialises this decision with any other work on the payment.
        var saga = await sagas.GetForUpdateAsync(paymentRef.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {command.PaymentId} has no saga.");
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);

        // The decision is the cause of what follows (the network call, or the release).
        var decisionId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        if (command.Approve)
        {
            saga.OnApproved(payment, command.OfficerUserId, command.OfficerLabel, command.OfficerClearance, approvalLimits,
                command.Remarks, decisionId, now);
        }
        else
        {
            saga.OnApprovalRejected(payment, command.OfficerUserId, command.OfficerLabel, command.Remarks ?? string.Empty,
                decisionId, sagaPolicy, now);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Payment {PaymentNumber} {Decision} by {Officer}; saga now at {Step}.",
            payment.PaymentNumber, command.Approve ? "APPROVED" : "REJECTED", command.OfficerLabel, saga.Step.ToCode());

        return new DecidePaymentResult(payment.Status.ToCode(), saga.Step.ToCode());
    }
}