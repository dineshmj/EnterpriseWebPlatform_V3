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
    ReplyTimedOut,
    NetworkAnswerDiscarded,
    NetworkConfirmedSent,
    NetworkConfirmedNotSent,
    NetworkStatusUnknown
}

/// <summary>
/// The step runner's unit of work: ONE saga whose timer is due (SKIP LOCKED, so several API
/// instances share the work). The decision itself is always the saga's.
///
/// A reply that did not come is handled in one short transaction. A payment network call is
/// never made inside a transaction (a row lock held for a network round trip would block the
/// saga's replies and exhaust the connection pool under a slow network):
///   1. claim   - short transaction: lock the saga, move its timer on by a lease, commit;
///   2. call    - the payment network, with no transaction and no lock held;
///   3. record  - short transaction: lock the saga again; if it is still the one claimed (same
///                Version), the saga decides on the answer; its next command goes to the Outbox.
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
    /// <summary>
    /// How long a claimed network attempt keeps other step runners away: well beyond the network
    /// client's total request timeout (20 s), so it only runs out when this process died mid-call.
    /// </summary>
    public static readonly TimeSpan NetworkLease = TimeSpan.FromSeconds(60);

    public async Task<SagaStepRunOutcome> HandleAsync(CancellationToken cancellationToken)
    {
        var claim = await ClaimNextDueAsync(cancellationToken);
        if (claim is not NetworkClaim networkClaim)
            return claim is ReplyTimeoutHandled ? SagaStepRunOutcome.ReplyTimedOut : SagaStepRunOutcome.NothingDue;

        using var trace = sagaTrace.Continue(networkClaim.Saga);
        var answer = networkClaim.IsStatusEnquiry
            ? await AskNetworkAsync(networkClaim.Request.IdempotencyKey, cancellationToken)
            : await CallNetworkAsync(networkClaim.Request, cancellationToken);
        return await RecordAnswerAsync(networkClaim, answer, cancellationToken);
    }

    // ------------------------------------------------------------------ 1. claim (or a reply timeout)

    private abstract record Claim;

    private sealed record ReplyTimeoutHandled : Claim;

    /// <summary>A claimed network call: the send itself, or (IsStatusEnquiry) the enquiry after the sends got no answer.</summary>
    private sealed record NetworkClaim(PaymentSaga Saga, Guid PaymentRef, long Version, string PaymentNumber, NetworkPaymentRequest Request, bool IsStatusEnquiry) : Claim;

    private async Task<Claim?> ClaimNextDueAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var saga = await sagas.GetNextDueAsync(clock.GetUtcNow(), cancellationToken);
        if (saga is null)
            return null;

        using var trace = sagaTrace.Continue(saga);
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        Claim claim;

        if (saga.IsDueForNetwork(now) || saga.IsDueForNetworkCheck(now))
        {
            var isStatusEnquiry = saga.IsDueForNetworkCheck(now);
            var version = saga.ClaimNetworkAttempt(NetworkLease, now);
            claim = new NetworkClaim(saga, saga.PaymentRef, version, payment.PaymentNumber, new NetworkPaymentRequest(
                payment.PaymentRef, payment.PaymentNumber, payment.From.Bsb, payment.From.AccountNumber, payment.PayeeName,
                payment.To.Bsb, payment.To.AccountNumber, payment.Amount, payment.Currency, payment.Reference), isStatusEnquiry);
        }
        else if (saga.IsWaitingForReply(now))
        {
            logger.LogWarning("Saga {SagaId} of payment {PaymentNumber}: no reply at step {Step} (attempt {Attempt}).",
                saga.Id, payment.PaymentNumber, saga.Step.ToCode(), saga.Attempts);
            saga.OnReplyTimeout(payment, policy, now);
            claim = new ReplyTimeoutHandled();
        }
        else
        {
            return null;
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    // ------------------------------------------------------------------ 2. call (no transaction)

    private abstract record NetworkAnswer;

    private sealed record Accepted(string Reference) : NetworkAnswer;

    private sealed record Refused(string Reason) : NetworkAnswer;

    private sealed record Unavailable(string Error) : NetworkAnswer;

    private sealed record ConfirmedSent(string Reference) : NetworkAnswer;

    private sealed record ConfirmedNotSent(string? RefusalReason) : NetworkAnswer;

    private sealed record StatusUnknown(string Error) : NetworkAnswer;

    private async Task<NetworkAnswer> AskNetworkAsync(Guid idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            var status = await network.GetStatusAsync(idempotencyKey, cancellationToken);
            return status.State switch
            {
                NetworkPaymentState.Sent => new ConfirmedSent(status.NetworkReference!),
                NetworkPaymentState.Refused => new ConfirmedNotSent(status.Reason ?? "refused"),
                _ => new ConfirmedNotSent(null)
            };
        }
        catch (PaymentNetworkUnavailableException ex)
        {
            return new StatusUnknown(ex.Message);
        }
    }

    private async Task<NetworkAnswer> CallNetworkAsync(NetworkPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return new Accepted(await network.SendAsync(request, cancellationToken));
        }
        catch (PaymentNetworkRefusedException ex)
        {
            return new Refused(ex.Message);
        }
        catch (PaymentNetworkUnavailableException ex)
        {
            return new Unavailable(ex.Message);
        }
    }

    // ------------------------------------------------------------------ 3. record

    private async Task<SagaStepRunOutcome> RecordAnswerAsync(NetworkClaim claim, NetworkAnswer answer, CancellationToken cancellationToken)
    {
        // Read the saga afresh (not the copy from the claim), under its row lock.
        unitOfWork.DiscardChanges();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var saga = await sagas.GetForUpdateAsync(claim.PaymentRef, cancellationToken);
        if (saga is null || saga.Version != claim.Version)
        {
            // Only possible when the lease ran out and another step runner took the attempt: its
            // answer counts. The network's Idempotency-Key kept the payment single either way.
            logger.LogWarning("Payment {PaymentNumber}: the saga moved on while the network call ran (claim expired); this answer ({Answer}) is not applied.",
                claim.PaymentNumber, answer.GetType().Name);
            return SagaStepRunOutcome.NetworkAnswerDiscarded;
        }

        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        SagaStepRunOutcome outcome;

        switch (answer)
        {
            case Accepted accepted:
                saga.OnNetworkAccepted(payment, accepted.Reference, policy, now);
                logger.LogInformation("Payment {PaymentNumber} accepted by the payment network ({Reference}); settling the funds.",
                    payment.PaymentNumber, accepted.Reference);
                outcome = SagaStepRunOutcome.NetworkAccepted;
                break;

            case Refused refused:
                saga.OnNetworkRefused(payment, refused.Reason, policy, now);
                logger.LogWarning("Payment {PaymentNumber} REFUSED by the payment network: {Reason}. Releasing the funds.",
                    payment.PaymentNumber, refused.Reason);
                outcome = SagaStepRunOutcome.NetworkRefused;
                break;

            case Unavailable unavailable:
                saga.OnNetworkUnavailable(payment, unavailable.Error, policy, now);
                logger.LogWarning("Payment {PaymentNumber}: payment network unavailable (attempt {Attempt} of {Max}): {Reason}. Saga now at {Step}.",
                    payment.PaymentNumber, saga.Step == SagaStep.SendToNetwork ? saga.Attempts : policy.MaxNetworkAttempts,
                    policy.MaxNetworkAttempts, unavailable.Error, saga.Step.ToCode());
                outcome = SagaStepRunOutcome.NetworkUnavailable;
                break;

            case ConfirmedSent confirmed:
                saga.OnNetworkConfirmedSent(payment, confirmed.Reference, policy, now);
                logger.LogWarning("Payment {PaymentNumber}: the network never answered, but confirms it SENT the payment ({Reference}); settling, not releasing.",
                    payment.PaymentNumber, confirmed.Reference);
                outcome = SagaStepRunOutcome.NetworkConfirmedSent;
                break;

            case ConfirmedNotSent notSent:
                saga.OnNetworkConfirmedNotSent(payment, notSent.RefusalReason, policy, now);
                logger.LogWarning("Payment {PaymentNumber}: the network confirms it did not send the payment{Reason}; releasing the funds.",
                    payment.PaymentNumber, notSent.RefusalReason is null ? string.Empty : $" (refused: {notSent.RefusalReason})");
                outcome = SagaStepRunOutcome.NetworkConfirmedNotSent;
                break;

            case StatusUnknown unknown:
                saga.OnNetworkStatusUnknown(unknown.Error, policy, now);
                logger.LogWarning("Payment {PaymentNumber}: the network cannot say whether it sent the payment ({Reason}); funds stay reserved, asking again.",
                    payment.PaymentNumber, unknown.Error);
                outcome = SagaStepRunOutcome.NetworkStatusUnknown;
                break;

            default:
                throw new InvalidOperationException($"Unknown network answer {answer.GetType().Name}.");
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }
}