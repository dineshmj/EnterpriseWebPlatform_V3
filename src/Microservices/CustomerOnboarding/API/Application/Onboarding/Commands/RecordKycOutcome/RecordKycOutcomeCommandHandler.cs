using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

// ┌─ :: RESILIENCE :: Idempotent consumer of workflow outcomes ────────────────────────────────────
// │ Why : KYC, Compliance and Accounts outcomes arrive through Kafka at least once; a repeat must
// │       not move the application twice.
// │ How : The message ID is recorded in inbox_messages in the same transaction as the state change;
// │       a repeat is answered and ignored.
// │ Ref : Features 1.3.2 | Idempotent Consumer pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class RecordKycOutcomeCommandHandler
{
    /// <summary>Inbox consumer name for KYC outcomes.</summary>
    public const string InboxConsumer = "customer-onboarding.kyc-outcomes";

    private readonly IOnboardingApplicationRepository _applicationRepository;

    private readonly IInboxStore _inbox;

    private readonly IApplicationUnitOfWork _unitOfWork;

    private readonly TimeProvider _clock;

    private readonly CustomerLifecycleSync _customerLifecycle;

    public RecordKycOutcomeCommandHandler(
        IOnboardingApplicationRepository applicationRepository,
        IInboxStore inbox,
        IApplicationUnitOfWork unitOfWork,
        TimeProvider clock,
        CustomerLifecycleSync customerLifecycle)
    {
        _applicationRepository = applicationRepository;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _customerLifecycle = customerLifecycle;
    }

    public async Task<RecordKycOutcomeResult> HandleAsync(
        RecordKycOutcomeCommand command,
        CancellationToken cancellationToken)
    {
        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ If this outcome was already recorded, do nothing (Inbox) - a redelivery never changes
        // │ the application twice.
        // └──────────────────────────────────────────────────────────────────────────────────────
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

        if (changed)
        {
            await _customerLifecycle.ApplyAsync(application, now, cancellationToken);
        }

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ The Inbox record, the application's (and customer's) new state and the resulting Outbox
        // │ events (OnboardingApplicationStatusChanged) commit in ONE transaction.
        // │ Even a no-op is recorded, so the message is never evaluated twice.
        // └──────────────────────────────────────────────────────────────────────────────────────
        _inbox.RecordProcessed(command.MessageId, InboxConsumer);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return changed ? RecordKycOutcomeResult.Applied : RecordKycOutcomeResult.NoChange;
    }
}