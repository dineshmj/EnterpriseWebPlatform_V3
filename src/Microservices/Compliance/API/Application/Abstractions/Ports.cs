using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;

/// <summary>Loads and adds ComplianceCase aggregates.</summary>
public interface IComplianceCaseRepository
{
    Task<ComplianceCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken);

    /// <summary>Loads the case and locks its row until the transaction ends (requires an open transaction).</summary>
    Task<ComplianceCase?> GetForUpdateAsync(long caseId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks and loads ONE case that is due for (re)screening, skipping cases another
    /// instance is already screening (FOR UPDATE SKIP LOCKED). Null when none is due.
    /// </summary>
    Task<ComplianceCase?> GetNextDueForScreeningAsync(DateTimeOffset now, CancellationToken cancellationToken);

    void Add(ComplianceCase complianceCase);
}

/// <summary>Workflow metadata of the command being handled (see the Event Catalogue).</summary>
public sealed record WorkflowContext(Guid? WorkflowId, Guid? CorrelationId, Guid CausationId);

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IComplianceUnitOfWork
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

// ---------------------------------------------------------------------------- Screening

/// <summary>What the Compliance context sends to the external screening provider (minimal data).</summary>
public sealed record ScreeningRequest(string CustomerNumber, string ApplicationNumber, Guid RequestId);

/// <summary>The provider's verdict.</summary>
public sealed record ScreeningResponse(ScreeningOutcome Outcome, string Provider, string Reference);

/// <summary>
/// The external AML / sanctions / PEP screening provider (an anti-corruption layer:
/// the provider's wire format never reaches the domain). Throws
/// <see cref="ScreeningUnavailableException"/> when it cannot answer.
/// </summary>
public interface IScreeningProvider
{
    Task<ScreeningResponse> ScreenAsync(ScreeningRequest request, CancellationToken cancellationToken);
}

/// <summary>The provider is down, slow, failing, or the circuit breaker is open: retry later.</summary>
public sealed class ScreeningUnavailableException(string message, Exception? inner = null) : Exception(message, inner);