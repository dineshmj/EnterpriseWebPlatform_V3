using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

/// <summary>First pair of eyes: what the network's own records show happened to the payment.</summary>
public sealed record ProposeNetworkResolutionCommand(
    long PaymentId,
    NetworkOutcome Outcome,
    string? NetworkReference,
    string Evidence,
    string OperatorUserId,
    string OperatorLabel);

/// <summary>Second pair of eyes: approve (the saga settles or releases) or reject (with remarks) the proposal.</summary>
public sealed record DecideNetworkResolutionCommand(
    long PaymentId,
    bool Approve,
    string? Remarks,
    string OperatorUserId,
    string OperatorLabel);

public sealed record NetworkResolutionResult(string Status, string SagaStep);

/// <summary>
/// Manual reconciliation of a payment the payment network never confirmed (Threat-Model R-12):
/// operations, after the alert time, with four eyes. The saga decides everything - when it is
/// possible, who may decide, what follows - and records it on the timeline; the steps are
/// published, so the audit trail names both people. Any branch: the operations desk is central.
/// Saga, payment and the messages are saved in ONE transaction.
/// </summary>
public sealed class ReconcileNetworkOutcomeCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    SagaPolicy sagaPolicy,
    TimeProvider clock,
    ILogger<ReconcileNetworkOutcomeCommandHandler> logger)
{
    /// <summary>Null when the payment does not exist.</summary>
    public Task<NetworkResolutionResult?> ProposeAsync(ProposeNetworkResolutionCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.PaymentId, (saga, _, now) =>
        {
            saga.ProposeNetworkResolution(command.Outcome, command.NetworkReference, command.Evidence,
                command.OperatorUserId, command.OperatorLabel, sagaPolicy, now);
            return $"proposed {command.Outcome.ToCode()}";
        }, command.OperatorLabel, cancellationToken);

    /// <summary>Null when the payment does not exist.</summary>
    public Task<NetworkResolutionResult?> DecideAsync(DecideNetworkResolutionCommand command, CancellationToken cancellationToken) =>
        RunAsync(command.PaymentId, (saga, payment, now) =>
        {
            saga.DecideNetworkResolution(payment, command.Approve, command.OperatorUserId, command.OperatorLabel,
                command.Remarks, sagaPolicy, now);
            return command.Approve ? "APPROVED the resolution" : "REJECTED the resolution";
        }, command.OperatorLabel, cancellationToken);

    private async Task<NetworkResolutionResult?> RunAsync(long paymentId, Func<PaymentSaga, Payment, DateTimeOffset, string> act,
        string operatorLabel, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var paymentRef = await payments.GetRefInBranchAsync(paymentId, branch: null, cancellationToken);
        if (paymentRef is null)
            return null;

        var saga = await sagas.GetForUpdateAsync(paymentRef.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {paymentId} has no saga.");
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);

        var what = act(saga, payment, clock.GetUtcNow());

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Manual reconciliation of payment {PaymentNumber}: {Operator} {What}; saga now at {Step}.",
            payment.PaymentNumber, operatorLabel, what, saga.Step.ToCode());
        return new NetworkResolutionResult(payment.Status.ToCode(), saga.Step.ToCode());
    }
}