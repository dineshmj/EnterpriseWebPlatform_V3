using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

public sealed class RecordKycOutcomeCommandHandler
{
    /// <summary>Inbox consumer name for KYC outcomes.</summary>
    public const string InboxConsumer = "customer-onboarding.kyc-outcomes";

    private readonly IOnboardingApplicationRepository _applicationRepository;

    private readonly IInboxStore _inbox;

    private readonly IApplicationUnitOfWork _unitOfWork;

    public RecordKycOutcomeCommandHandler(
        IOnboardingApplicationRepository applicationRepository,
        IInboxStore inbox,
        IApplicationUnitOfWork unitOfWork)
    {
        _applicationRepository = applicationRepository;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
    }

    public async Task<RecordKycOutcomeResult> HandleAsync(
        RecordKycOutcomeCommand command,
        CancellationToken cancellationToken)
    {
        if (await _inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
        {
            return RecordKycOutcomeResult.Duplicate;
        }

        var application = await _applicationRepository.GetByIdAsync(
            command.ApplicationId,
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

        bool changed;
        switch (command.EventType)
        {
            case "KycCaseCreated":
                changed = application.RecordKycCaseOpened();
                break;

            case "KycCaseApproved":
                changed = application.RecordKycApproved();
                break;

            case "KycCaseRejected":
                changed = application.RecordKycRejected();
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
