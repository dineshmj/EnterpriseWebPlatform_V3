using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;

/// <summary>Loads and adds KycCase aggregates. The only way the application layer reaches them.</summary>
public interface IKycCaseRepository
{
    Task<KycCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the case and locks its row until the current transaction ends, so
    /// concurrent decisions on the same case are serialised and each one sees the
    /// other stage's latest committed state. Requires an open transaction.
    /// </summary>
    Task<KycCase?> GetForDecisionAsync(long caseId, CancellationToken cancellationToken);

    void Add(KycCase kycCase);
}

/// <summary>
/// Workflow metadata of the command being handled. CausationId is the message or
/// command that caused this change; WorkflowId / CorrelationId are carried over
/// from the case's workflow when not supplied.
/// </summary>
public sealed record WorkflowContext(Guid? WorkflowId, Guid? CorrelationId, Guid CausationId);

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IKycUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the changed aggregates and writes the domain events they raised to the
    /// Outbox, in the order they were raised, in the current transaction.
    /// Throws <see cref="ConcurrencyConflictException"/> or
    /// <see cref="UniqueConstraintViolationException"/> for the corresponding database outcomes.
    /// </summary>
    Task SaveChangesAsync(WorkflowContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Inbox (idempotent consumer). A recorded message is saved in the SAME
/// transaction as the business change it caused, so a redelivered message is
/// recognised and ignored - "processed exactly once" on top of at-least-once delivery.
/// </summary>
public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    /// <summary>Records the message; persisted by the next unit-of-work save.</summary>
    void RecordProcessed(Guid messageId, string consumer);
}

/// <summary>Another transaction changed the aggregate first (optimistic concurrency).</summary>
public sealed class ConcurrencyConflictException(string message, Exception inner) : Exception(message, inner);

/// <summary>A unique constraint rejected the insert (e.g. a second case for the same application).</summary>
public sealed class UniqueConstraintViolationException(string message, Exception inner) : Exception(message, inner);