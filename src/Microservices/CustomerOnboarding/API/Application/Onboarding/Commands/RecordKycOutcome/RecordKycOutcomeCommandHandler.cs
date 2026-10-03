using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

public sealed class RecordKycOutcomeCommandHandler
{
    /// <summary>Inbox consumer name for KYC outcomes.</summary>
    public const string InboxConsumer = "customer-onboarding.kyc-outcomes";

    private readonly IOnboardingApplicationRepository _applicationRepository;

    private readonly IInboxStore _inbox;

    private readonly IApplicationUnitOfWork _unitOfWork;

    private readonly TimeProvider _clock;

    public RecordKycOutcomeCommandHandler(
        IOnboardingApplicationRepository applicationRepository,
        IInboxStore inbox,
        IApplicationUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _applicationRepository = applicationRepository;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<RecordKycOutcomeResult> HandleAsync(
        RecordKycOutcomeCommand command,
        CancellationToken cancellationToken)
    {
        if (await _inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
        {
            return RecordKycOutcomeResult.Duplicate;
        }

        var application = await _applicationRepository.GetByRefAsync(
            command.ApplicationRef,
            cancellationToken);

        if (application is null)
        {
            return RecordKycOutcomeResult.NotFound;
        }

        if (!string.Equals(
                application.ApplicationNumber.Value,
                command.ApplicationNumber.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return RecordKycOutcomeResult.ApplicationMismatch;
        }

        var now = _clock.GetUtcNow();

        bool changed;
        switch (command.EventType)
        {
            case "KycCaseCreated":
                changed = application.RecordKycCaseOpened(now);
                break;

            case "KycCaseApproved":
                changed = application.RecordKycApproved(now);
                break;

            case "KycCaseRejected":
                changed = application.RecordKycRejected(now);
                break;

            default:
                return RecordKycOutcomeResult.UnsupportedEventType;
        }

        // The Inbox record, the application's new state and the resulting Outbox
        // events (OnboardingApplicationStatusChanged) commit in ONE transaction.
        // Even a no-op is recorded, so the message is never evaluated twice.
        _inbox.RecordProcessed(command.MessageId, InboxConsumer);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return changed ? RecordKycOutcomeResult.Applied : RecordKycOutcomeResult.NoChange;
    }
}
