using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordAccountOutcome;

/// <summary>
/// Applies an Accounts fact (AccountApplicationCreated / AccountOpened /
/// AccountApplicationRejected) to an onboarding application. MessageId is the Accounts
/// event's MessageId (idempotency key); ApplicationNumber is a consistency check.
/// </summary>
public sealed record RecordAccountOutcomeCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    Guid MessageId,
    string EventType);

/// <summary>Same outcomes as for KYC and Compliance facts (Applied, NoChange, Duplicate, NotFound, ...).</summary>
public sealed class RecordAccountOutcomeCommandHandler(
    IOnboardingApplicationRepository applicationRepository,
    IInboxStore inbox,
    IApplicationUnitOfWork unitOfWork,
    TimeProvider clock,
    CustomerLifecycleSync customerLifecycle)
{
    /// <summary>Inbox consumer name for Accounts outcomes.</summary>
    public const string InboxConsumer = "customer-onboarding.account-outcomes";

    public async Task<RecordKycOutcomeResult> HandleAsync(RecordAccountOutcomeCommand command, CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
            return RecordKycOutcomeResult.Duplicate;

        var application = await applicationRepository.GetByRefAsync(command.ApplicationRef, cancellationToken);
        if (application is null)
            return RecordKycOutcomeResult.NotFound;

        if (!string.Equals(application.ApplicationNumber.Value, command.ApplicationNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            return RecordKycOutcomeResult.ApplicationMismatch;

        var now = clock.GetUtcNow();
        bool changed;
        switch (command.EventType)
        {
            case "AccountApplicationCreated": changed = application.RecordAccountApplicationCreated(now); break;
            case "AccountOpened": changed = application.RecordAccountOpened(now); break;
            case "AccountApplicationRejected": changed = application.RecordAccountApplicationRejected(now); break;
            default: return RecordKycOutcomeResult.UnsupportedEventType;
        }

        if (changed)
            await customerLifecycle.ApplyAsync(application, now, cancellationToken);

        // Inbox record, new application (and customer) state and its Outbox events: ONE transaction.
        inbox.RecordProcessed(command.MessageId, InboxConsumer);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return changed ? RecordKycOutcomeResult.Applied : RecordKycOutcomeResult.NoChange;
    }
}