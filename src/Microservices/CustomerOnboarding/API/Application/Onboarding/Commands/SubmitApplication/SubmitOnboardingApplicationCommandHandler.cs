using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.SubmitApplication;

public sealed class SubmitOnboardingApplicationCommandHandler
{
    private readonly IOnboardingApplicationRepository _applicationRepository;

    private readonly IApplicationUnitOfWork _unitOfWork;

    private readonly TimeProvider _clock;

    public SubmitOnboardingApplicationCommandHandler(
        IOnboardingApplicationRepository applicationRepository,
        IApplicationUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _applicationRepository = applicationRepository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task HandleAsync(
        SubmitOnboardingApplicationCommand command,
        CancellationToken cancellationToken)
    {
        var application = await _applicationRepository.GetByIdAsync(
            command.ApplicationId,
            cancellationToken);

        if (application is null)
        {
            throw new KeyNotFoundException(
                $"Onboarding application '{command.ApplicationId}' was not found.");
        }

        if (application.Version != command.ExpectedVersion)
        {
            throw new InvalidOperationException(
                "The onboarding application was modified by another request.");
        }

        application.Submit(_clock.GetUtcNow());

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}