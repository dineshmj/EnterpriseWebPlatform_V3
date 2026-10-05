using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;

/// <summary>Loads and adds AccountApplication aggregates.</summary>
public interface IAccountApplicationRepository
{
    Task<AccountApplication?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken);

    /// <summary>Loads the application and locks its row until the transaction ends (requires an open transaction).</summary>
    Task<AccountApplication?> GetForUpdateAsync(long applicationId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks and loads ONE application that is due for (re)opening, skipping applications
    /// another instance is already opening (FOR UPDATE SKIP LOCKED). Null when none is due.
    /// </summary>
    Task<AccountApplication?> GetNextDueForOpeningAsync(DateTimeOffset now, CancellationToken cancellationToken);

    void Add(AccountApplication application);
}

/// <summary>Adds Account aggregates.</summary>
public interface IAccountRepository
{
    void Add(Account account);
}

/// <summary>Workflow metadata of the command being handled (see the Event Catalogue).</summary>
public sealed record WorkflowContext(Guid? WorkflowId, Guid? CorrelationId, Guid CausationId);

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IAccountsUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the changed aggregates and writes their published domain events to the
    /// Outbox, in order, in the current transaction.
    /// </summary>
    Task SaveChangesAsync(WorkflowContext context, CancellationToken cancellationToken);
}

/// <summary>Inbox (idempotent consumer): recorded in the same transaction as the change it caused.</summary>
public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);

    void RecordProcessed(Guid messageId, string consumer);
}

public sealed class ConcurrencyConflictException(string message, Exception inner) : Exception(message, inner);

public sealed class UniqueConstraintViolationException(string message, Exception inner) : Exception(message, inner);

// ---------------------------------------------------------------------------- Core banking

/// <summary>
/// What Accounts asks the core-banking system to do. <see cref="IdempotencyKey"/> (the
/// onboarding ApplicationRef) makes a repeated request return the SAME account, so a
/// retry after a timeout can never open a second account.
/// </summary>
public sealed record OpenAccountRequest(Guid IdempotencyKey, string CustomerNumber, string BranchCode, AccountProduct Product);

/// <summary>The account the core-banking system opened.</summary>
public sealed record OpenAccountResponse(string AccountNumber, string Bsb, string CoreBankingReference);

/// <summary>
/// The external core-banking system (an anti-corruption layer: its wire format never
/// reaches the domain). Throws <see cref="CoreBankingUnavailableException"/> when it
/// cannot answer (retry later) and <see cref="CoreBankingRefusedException"/> when it
/// answers "no" (permanent).
/// </summary>
public interface ICoreBankingSystem
{
    Task<OpenAccountResponse> OpenAccountAsync(OpenAccountRequest request, CancellationToken cancellationToken);
}

/// <summary>Core banking is down, slow, failing, or the circuit breaker is open: retry later.</summary>
public sealed class CoreBankingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Core banking refused to open the account: a permanent answer, no retry.</summary>
public sealed class CoreBankingRefusedException(string message) : Exception(message);