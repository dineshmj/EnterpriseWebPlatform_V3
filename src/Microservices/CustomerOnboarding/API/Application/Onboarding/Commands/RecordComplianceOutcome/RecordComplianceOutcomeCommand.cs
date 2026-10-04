using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordComplianceOutcome;

/// <summary>
/// Applies a Compliance fact (ComplianceCaseCreated / ComplianceCaseApproved /
/// ComplianceCaseRejected) to an onboarding application. MessageId is the Compliance
/// event's MessageId (idempotency key); ApplicationNumber is a consistency check.
/// </summary>
public sealed record RecordComplianceOutcomeCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    Guid MessageId,
    string EventType);

/// <summary>Same outcomes as for KYC facts (Applied, NoChange, Duplicate, NotFound, ...).</summary>
public sealed class RecordComplianceOutcomeCommandHandler(
    IOnboardingApplicationRepository applicationRepository,
    IInboxStore inbox,
    IApplicationUnitOfWork unitOfWork,
    TimeProvider clock)
{
    /// <summary>Inbox consumer name for Compliance outcomes.</summary>
    public const string InboxConsumer = "customer-onboarding.compliance-outcomes";

    public async Task<RecordKycOutcomeResult> HandleAsync(RecordComplianceOutcomeCommand command, CancellationToken cancellationToken)
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
            case "ComplianceCaseCreated": changed = application.RecordComplianceCaseOpened(now); break;
            case "ComplianceCaseApproved": changed = application.RecordComplianceApproved(now); break;
            case "ComplianceCaseRejected": changed = application.RecordComplianceRejected(now); break;
            default: return RecordKycOutcomeResult.UnsupportedEventType;
        }

        // Inbox record, new application state and its Outbox events: ONE transaction.
        inbox.RecordProcessed(command.MessageId, InboxConsumer);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return changed ? RecordKycOutcomeResult.Applied : RecordKycOutcomeResult.NoChange;
    }
}