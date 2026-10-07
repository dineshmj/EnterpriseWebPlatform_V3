using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;

namespace EnterpriseWebPlatform.Payments.Api.Application.Abstractions;

/// <summary>Loads and adds Payment aggregates.</summary>
public interface IPaymentRepository
{
    Task<Payment?> GetByRefAsync(Guid paymentRef, CancellationToken cancellationToken);

    Task<Payment> GetAsync(long paymentId, CancellationToken cancellationToken);

    /// <summary>The payment's PaymentRef when it exists (in this branch, when one is given); null otherwise.</summary>
    Task<Guid?> GetRefInBranchAsync(long paymentId, Domain.ValueObjects.BranchCode? branch, CancellationToken cancellationToken);

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

    /// <summary>The bank and branch a BSB belongs to (the network's BSB directory); null when the BSB is unknown.</summary>
    Task<BsbInfo?> LookupBsbAsync(string bsb, CancellationToken cancellationToken);

    /// <summary>Confirmation of Payee: does this name match the account at the payee's bank?</summary>
    Task<PayeeConfirmation> ConfirmPayeeAsync(string bsb, string accountNumber, string accountName, CancellationToken cancellationToken);
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