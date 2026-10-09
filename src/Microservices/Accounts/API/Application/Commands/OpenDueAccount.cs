using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

public sealed record OpeningRetryPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay, int MaxAttempts)
{
    /// <summary>Exponential back-off: initial × 2^(attempts-1), capped.</summary>
    public TimeSpan DelayAfter(int failedAttempts) =>
        TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(2, Math.Max(0, Math.Min(failedAttempts - 1, 16)))));
}

public enum OpeningRunOutcome
{
    NothingDue,
    Opened,
    Failed,
    CoreBankingUnavailable
}

/// <summary>
/// Opens ONE approved account that is due: locks the application (SKIP LOCKED, so several
/// API instances can share the work), asks the core-banking system, and records the
/// result in the same transaction: OPENED (and the new Account), a retry time, or FAILED.
/// The ApplicationRef is the idempotency key towards core banking, so a retry after a
/// lost answer returns the same account instead of opening a second one.
/// </summary>
public sealed class OpenDueAccountCommandHandler(
    IAccountApplicationRepository applications,
    IAccountRepository accounts,
    IAccountsUnitOfWork unitOfWork,
    ICoreBankingSystem coreBanking,
    IOpeningTrace openingTrace,
    OpeningRetryPolicy retryPolicy,
    AccountOpeningDeposit openingDeposit,
    TimeProvider clock,
    ILogger<OpenDueAccountCommandHandler> logger)
{
    public async Task<OpeningRunOutcome> HandleAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var application = await applications.GetNextDueForOpeningAsync(clock.GetUtcNow(), cancellationToken);
        if (application is null)
            return OpeningRunOutcome.NothingDue;

        // Anything other than core banking's own answer (e.g. the database refusing the new
        // account) must not leave the application due with no attempt recorded: it would be picked
        // again every cycle, forever, ahead of every other application. Hand it to the worker,
        // which records it as a failed attempt in a fresh transaction (RecordOpeningErrorCommandHandler).
        try
        {
            return await OpenAsync(application, transaction, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AccountOpeningErrorException(application.Id, application.ApplicationNumber, ex);
        }
    }

    private async Task<OpeningRunOutcome> OpenAsync(AccountApplication application, IUnitOfWorkTransaction transaction, CancellationToken cancellationToken)
    {
        // The core-banking call and the resulting events join the approval's trace.
        using var trace = openingTrace.Continue(application);

        OpeningRunOutcome outcome;
        try
        {
            var response = await coreBanking.OpenAccountAsync(
                new OpenAccountRequest(application.ApplicationRef, application.CustomerNumber, application.HolderName.FullName, application.BranchCode.Value, application.Product!.Value),
                cancellationToken);

            var now = clock.GetUtcNow();
            application.RecordAccountOpened(response.AccountNumber, response.Bsb, now);
            accounts.Add(Account.Open(response.AccountNumber, response.Bsb, response.CoreBankingReference, application, openingDeposit.Amount, now));
            outcome = OpeningRunOutcome.Opened;

            logger.LogInformation(
                "Account {Bsb} {AccountNumber} opened for application {ApplicationNumber} ({Product}).",
                response.Bsb, response.AccountNumber, application.ApplicationNumber, application.Product!.Value.ToCode());
        }
        catch (CoreBankingRefusedException ex)
        {
            application.RecordOpeningRefused(ex.Message, clock.GetUtcNow());
            outcome = OpeningRunOutcome.Failed;

            logger.LogWarning(
                "Core banking REFUSED the account for application {ApplicationNumber}: {Reason}. The application is FAILED.",
                application.ApplicationNumber, ex.Message);
        }
        catch (CoreBankingUnavailableException ex)
        {
            var now = clock.GetUtcNow();
            var delay = retryPolicy.DelayAfter(application.OpeningAttempts + 1);
            application.RecordOpeningFailure(ex.Message, retryPolicy.MaxAttempts, now + delay, now);

            if (application.Status == AccountApplicationStatus.Failed)
            {
                outcome = OpeningRunOutcome.Failed;
                logger.LogError(
                    "Account opening for application {ApplicationNumber} failed {Attempts} times and is given up (FAILED): {Reason}",
                    application.ApplicationNumber, application.OpeningAttempts, ex.Message);
            }
            else
            {
                outcome = OpeningRunOutcome.CoreBankingUnavailable;
                logger.LogWarning(
                    "Account opening for application {ApplicationNumber} failed (attempt {Attempt} of {Max}): {Reason}. Still OPENING; next attempt in {Delay}.",
                    application.ApplicationNumber, application.OpeningAttempts, retryPolicy.MaxAttempts, ex.Message, delay);
            }
        }

        // The opening continues the workflow the application was opened in, and is caused
        // by the officer's approval (not by this background run).
        await unitOfWork.SaveChangesAsync(
            new WorkflowContext(null, null, application.DecisionId ?? Guid.NewGuid()),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }
}

/// <summary>
/// Opening an account failed for a reason other than core banking's answer - typically the
/// database refusing the result. Carries the application, so the failure can be recorded on it.
/// </summary>
public sealed class AccountOpeningErrorException(long applicationId, string applicationNumber, Exception inner)
    : Exception($"Opening the account for application {applicationNumber} failed unexpectedly.", inner)
{
    public long ApplicationId { get; } = applicationId;

    public string ApplicationNumber { get; } = applicationNumber;
}

/// <summary>
/// Records an unexpected opening error as a failed attempt, in its own transaction (the opening's
/// transaction was rolled back): the attempt is counted, the next try is scheduled with back-off,
/// and after the usual limit the application is FAILED (and compensated) instead of blocking the
/// queue. The recorded reason names the error's kind only; the details stay in the log, because
/// the reason also appears in the readiness check, which is anonymous.
/// </summary>
public sealed class RecordOpeningErrorCommandHandler(
    IAccountApplicationRepository applications,
    IAccountsUnitOfWork unitOfWork,
    OpeningRetryPolicy retryPolicy,
    TimeProvider clock)
{
    public async Task HandleAsync(long applicationId, Exception error, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var application = await applications.GetForUpdateAsync(applicationId, cancellationToken);
        if (application is null || application.Status != AccountApplicationStatus.Opening)
            return;

        var now = clock.GetUtcNow();
        application.RecordOpeningFailure(
            $"Unexpected error while opening the account ({error.GetType().Name}); see the Accounts API log.",
            retryPolicy.MaxAttempts,
            now + retryPolicy.DelayAfter(application.OpeningAttempts + 1),
            now);

        await unitOfWork.SaveChangesAsync(new WorkflowContext(null, null, application.DecisionId ?? Guid.NewGuid()), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}