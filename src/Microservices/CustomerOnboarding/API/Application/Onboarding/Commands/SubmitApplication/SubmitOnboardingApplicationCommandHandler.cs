using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

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
            throw new ConcurrencyConflictException(
                "The onboarding application was modified by another request.");
        }

        var evidence = command.EvidenceDocuments
            .Select(x => EvidenceDocument.Create(x.DocumentId, x.DocumentType))
            .ToList();

        application.Submit(evidence, _clock.GetUtcNow());

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}