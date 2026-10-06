using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

public enum SagaStepRunOutcome
{
    NothingDue,
    NetworkAccepted,
    NetworkRefused,
    NetworkUnavailable,
    ReplyTimedOut
}

/// <summary>
/// The step runner's unit of work: locks ONE saga whose timer is due (SKIP LOCKED, so
/// several API instances share the work) and does what the step needs - call the payment
/// network, or react to a reply that did not come - then saves saga, payment and the next
/// command in the same transaction. The decision itself is always the saga's.
/// </summary>
public sealed class RunDueSagaStepCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    IPaymentNetwork network,
    ISagaTrace sagaTrace,
    SagaPolicy policy,
    TimeProvider clock,
    ILogger<RunDueSagaStepCommandHandler> logger)
{
    public async Task<SagaStepRunOutcome> HandleAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var saga = await sagas.GetNextDueAsync(clock.GetUtcNow(), cancellationToken);
        if (saga is null)
            return SagaStepRunOutcome.NothingDue;

        // The network call and what follows join the trace of the request that started the payment.
        using var trace = sagaTrace.Continue(saga);
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        SagaStepRunOutcome outcome;

        if (saga.IsDueForNetwork(now))
        {
            outcome = await SendToNetworkAsync(saga, payment, cancellationToken);
        }
        else if (saga.IsWaitingForReply(now))
        {
            logger.LogWarning("Saga {SagaId} of payment {PaymentNumber}: no reply at step {Step} (attempt {Attempt}).",
                saga.Id, payment.PaymentNumber, saga.Step.ToCode(), saga.Attempts);
            saga.OnReplyTimeout(payment, policy, now);
            outcome = SagaStepRunOutcome.ReplyTimedOut;
        }
        else
        {
            return SagaStepRunOutcome.NothingDue;
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private async Task<SagaStepRunOutcome> SendToNetworkAsync(PaymentSaga saga, Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            var reference = await network.SendAsync(
                new NetworkPaymentRequest(
                    payment.PaymentRef, payment.PaymentNumber, payment.From.Bsb, payment.From.AccountNumber, payment.PayeeName,
                    payment.To.Bsb, payment.To.AccountNumber, payment.Amount, payment.Currency, payment.Reference),
                cancellationToken);

            saga.OnNetworkAccepted(payment, reference, policy, clock.GetUtcNow());
            logger.LogInformation("Payment {PaymentNumber} accepted by the payment network ({Reference}); settling the funds.",
                payment.PaymentNumber, reference);
            return SagaStepRunOutcome.NetworkAccepted;
        }
        catch (PaymentNetworkRefusedException ex)
        {
            saga.OnNetworkRefused(payment, ex.Message, policy, clock.GetUtcNow());
            logger.LogWarning("Payment {PaymentNumber} REFUSED by the payment network: {Reason}. Releasing the funds.",
                payment.PaymentNumber, ex.Message);
            return SagaStepRunOutcome.NetworkRefused;
        }
        catch (PaymentNetworkUnavailableException ex)
        {
            saga.OnNetworkUnavailable(payment, ex.Message, policy, clock.GetUtcNow());
            logger.LogWarning("Payment {PaymentNumber}: payment network unavailable (attempt {Attempt} of {Max}): {Reason}. Saga now at {Step}.",
                payment.PaymentNumber, saga.Step == SagaStep.SendToNetwork ? saga.Attempts : policy.MaxNetworkAttempts,
                policy.MaxNetworkAttempts, ex.Message, saga.Step.ToCode());
            return SagaStepRunOutcome.NetworkUnavailable;
        }
    }
}