using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;

namespace EnterpriseWebPlatform.Payments.Api.Application.Abstractions;

/// <summary>Loads and adds Payment aggregates.</summary>
public interface IPaymentRepository
{
    Task<Payment?> GetByRefAsync(Guid paymentRef, CancellationToken cancellationToken);

    Task<Payment> GetAsync(long paymentId, CancellationToken cancellationToken);

    /// <summary>The payment's PaymentRef when it exists (in this branch, when one is given); null otherwise.</summary>
    Task<Guid?> GetRefInBranchAsync(long paymentId, Domain.ValueObjects.BranchCode? branch, CancellationToken cancellationToken);

    /// <summary>Whether this customer already has a COMPLETED payment to this payee account (the fraud check's "new payee").</summary>
    Task<bool> HasPaidBeforeAsync(string customerNumber, Domain.ValueObjects.BankAccountRef payee, CancellationToken cancellationToken);

    void Add(Payment payment);
}

/// <summary>Loads (locked) and adds PaymentSaga aggregates. Every change to a payment goes through its saga's row lock.</summary>
public interface IPaymentSagaRepository
{
    /// <summary>The payment's saga, locked until the transaction ends (replies queue here).</summary>
    Task<PaymentSaga?> GetForUpdateAsync(Guid paymentRef, CancellationToken cancellationToken);

    /// <summary>
    /// Locks and loads ONE running saga whose timer is due (a network call, or a reply that
    /// did not come), skipping sagas another instance is working on (FOR UPDATE SKIP LOCKED).
    /// </summary>
    Task<PaymentSaga?> GetNextDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    void Add(PaymentSaga saga);
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IPaymentsUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the changed aggregates and writes the saga's commands and the payment's
    /// published events to the Outbox, in order, in the current transaction.
    /// </summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Forgets every pending change (after a failed transaction, before trying again).</summary>
    void DiscardChanges();
}

/// <summary>A message the Outbox relay gave up on (PARKED), as operations see it.</summary>
public sealed record ParkedOutboxMessage(
    Guid MessageId,
    string EventType,
    string AggregateId,
    string? PaymentNumber,
    long? PaymentId,
    DateTimeOffset OccurredAt,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    int HeldBack,
    int RedriveCount,
    DateTimeOffset? LastRedrivenAt,
    string? LastRedrivenBy,
    string? LastRedriveReason);

/// <summary>
/// Parked Outbox messages: the list for operations, and the resend - the SAME message (same
/// id) goes back to the relay with fresh attempts, so a consumer that did receive an earlier
/// attempt drops it in its Inbox.
/// </summary>
public interface IParkedOutbox
{
    Task<IReadOnlyList<ParkedOutboxMessage>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The message's aggregate (the PaymentRef), parked or not; null when it does not exist.</summary>
    Task<string?> FindAggregateIdAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Locks the message until the transaction ends; null when it does not exist or is not parked.</summary>
    Task<ParkedOutboxMessage?> LockParkedAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Hands the locked message back to the relay (saved with the unit of work).</summary>
    Task ResendAsync(Guid messageId, string byLanId, string reason, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Inbox (idempotent consumer): recorded in the same transaction as the change it caused.</summary>
public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    void RecordProcessed(Guid messageId, string consumer);
}

/// <summary>
/// The context's staff directory: remembers a staff member's LAN ID (from their token) so
/// screens and events can show it. Identity and every rule stay on the subject ID.
/// </summary>
public interface IStaffDirectory
{
    Task RememberAsync(string? userId, string? lanId, CancellationToken cancellationToken);
}

/// <summary>
/// Continues the trace of the request that started the payment while the step runner
/// works on it in the background (infrastructure: the domain never sees trace context).
/// </summary>
public interface ISagaTrace
{
    IDisposable? Continue(PaymentSaga saga);
}

public sealed class ConcurrencyConflictException(string message, Exception inner) : Exception(message, inner);

public sealed class UniqueConstraintViolationException(string message, Exception inner) : Exception(message, inner);

// ---------------------------------------------------------------------------- The payment network

/// <summary>
/// What Payments sends to the payment network. <see cref="IdempotencyKey"/> (the PaymentRef)
/// makes a repeated request return the SAME result, so a retry after a timeout can never
/// pay twice.
/// </summary>
public sealed record NetworkPaymentRequest(
    Guid IdempotencyKey,
    string PaymentNumber,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    decimal Amount,
    string Currency,
    string? Reference);

/// <summary>
/// The external payment network (an anti-corruption layer: its wire format never reaches
/// the domain). Throws <see cref="PaymentNetworkUnavailableException"/> when it cannot
/// answer (retry later) and <see cref="PaymentNetworkRefusedException"/> when it answers
/// "no" (permanent). Returns the network's reference when it accepted the payment.
/// </summary>
public interface IPaymentNetwork
{
    Task<string> SendAsync(NetworkPaymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Status enquiry by Idempotency-Key, for an unknown outcome: did the network send this payment?
    /// Throws <see cref="PaymentNetworkUnavailableException"/> when it cannot answer that either.
    /// </summary>
    Task<NetworkPaymentStatus> GetStatusAsync(Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The bank and branch a BSB belongs to (the network's BSB directory); null when the BSB is unknown.</summary>
    Task<BsbInfo?> LookupBsbAsync(string bsb, CancellationToken cancellationToken);

    /// <summary>Confirmation of Payee: does this name match the account at the payee's bank?</summary>
    Task<PayeeConfirmation> ConfirmPayeeAsync(string bsb, string accountNumber, string accountName, CancellationToken cancellationToken);
}

/// <summary>The network's own record of a payment: sent (with its reference), refused (with why), or never received.</summary>
public sealed record NetworkPaymentStatus(NetworkPaymentState State, string? NetworkReference, string? Reason);

public enum NetworkPaymentState
{
    Sent = 1,
    Refused = 2,
    NotReceived = 3
}

/// <summary>A BSB as the directory knows it.</summary>
public sealed record BsbInfo(string Bsb, string Bank, string Branch, string State, bool AcceptsRealTimePayments);

/// <summary>
/// The payee bank's answer: MATCH, CLOSE_MATCH (with the name it holds) or NO_MATCH. A
/// warning for the staff member, never a decision: a mismatch must be confirmed with the
/// customer before sending.
/// </summary>
public sealed record PayeeConfirmation(string Result, string? AccountNameHeld);

/// <summary>The network is down, slow, failing, or the circuit breaker is open: retry later.</summary>
public sealed class PaymentNetworkUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The network refused the payment (e.g. the payee account is closed): permanent.</summary>
public sealed class PaymentNetworkRefusedException(string message) : Exception(message);

// ---------------------------------------------------------------------------- The fraud scoring

/// <summary>What the fraud scoring is told about a payment about to be made (nothing about the staff member).</summary>
public sealed record FraudScoringRequest(
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    decimal Amount,
    string Currency,
    bool FirstPaymentToPayee);

/// <summary>The provider's answer: a score 0-999, its decision and the reasons (codes).</summary>
public sealed record FraudScoringResult(int Score, Domain.ValueObjects.FraudDecision Decision, IReadOnlyList<string> Reasons);

/// <summary>
/// The payment network's fraud scoring (an anti-corruption layer: its wire format never reaches
/// the domain). Answers within the latency budget, or throws
/// <see cref="FraudScoringUnavailableException"/> - late, down, failing, an open circuit, or an
/// answer it cannot use. The caller then decides by its fallback rules.
/// </summary>
public interface IFraudScoring
{
    Task<FraudScoringResult> ScoreAsync(FraudScoringRequest request, CancellationToken cancellationToken);
}

/// <summary>No usable score within the latency budget: the fallback rules decide.</summary>
public sealed class FraudScoringUnavailableException(string message, Exception? inner = null) : Exception(message, inner);