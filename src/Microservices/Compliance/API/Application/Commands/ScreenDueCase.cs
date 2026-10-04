using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Application.Commands;

public sealed record ScreeningRetryPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay)
{
    /// <summary>Exponential back-off: initial × 2^(attempts-1), capped.</summary>
    public TimeSpan DelayAfter(int failedAttempts) =>
        TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(2, Math.Max(0, Math.Min(failedAttempts - 1, 16)))));
}

public enum ScreeningRunOutcome
{
    NothingDue,
    Screened,
    ProviderUnavailable
}

/// <summary>
/// Screens ONE case that is due: locks it (SKIP LOCKED, so several API instances can
/// share the work), asks the provider, and records the verdict or the failure in the
/// same transaction. A provider failure keeps the case in SCREENING with a later
/// retry time: it is never turned into a clear result.
/// </summary>
public sealed class ScreenDueCaseCommandHandler(
    IComplianceCaseRepository repository,
    IComplianceUnitOfWork unitOfWork,
    IScreeningProvider provider,
    ScreeningRetryPolicy retryPolicy,
    TimeProvider clock,
    ILogger<ScreenDueCaseCommandHandler> logger)
{
    public async Task<ScreeningRunOutcome> HandleAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var complianceCase = await repository.GetNextDueForScreeningAsync(clock.GetUtcNow(), cancellationToken);
        if (complianceCase is null)
            return ScreeningRunOutcome.NothingDue;

        ScreeningRunOutcome outcome;
        try
        {
            var response = await provider.ScreenAsync(
                new ScreeningRequest(complianceCase.CustomerNumber, complianceCase.ApplicationNumber, Guid.NewGuid()),
                cancellationToken);

            complianceCase.RecordScreeningResult(response.Outcome, response.Provider, response.Reference, clock.GetUtcNow());
            outcome = ScreeningRunOutcome.Screened;

            logger.LogInformation(
                "Compliance case {ComplianceCaseId} screened by {Provider}: {Outcome} -> risk {Risk} (approval needs clearance {Clearance}).",
                complianceCase.Id, response.Provider, response.Outcome.ToCode(),
                complianceCase.RiskRating!.Value.ToCode(), complianceCase.RequiredClearance);
        }
        catch (ScreeningUnavailableException ex)
        {
            var now = clock.GetUtcNow();
            var delay = retryPolicy.DelayAfter(complianceCase.ScreeningAttempts + 1);
            complianceCase.RecordScreeningFailure(ex.Message, now + delay, now);
            outcome = ScreeningRunOutcome.ProviderUnavailable;

            logger.LogWarning(
                "Screening of compliance case {ComplianceCaseId} failed (attempt {Attempt}): {Reason}. The case stays in SCREENING; next attempt in {Delay}.",
                complianceCase.Id, complianceCase.ScreeningAttempts, ex.Message, delay);
        }

        // Screening raises only internal events, so no Outbox row; the workflow
        // context is still required by the unit of work.
        await unitOfWork.SaveChangesAsync(new WorkflowContext(null, null, Guid.NewGuid()), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }
}