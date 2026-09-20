using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;

public sealed class CreateOnboardingApplicationCommandHandler
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IOnboardingApplicationRepository _applicationRepository;
    private readonly IApplicationUnitOfWork _unitOfWork;

    public CreateOnboardingApplicationCommandHandler(
        ICustomerRepository customerRepository,
        IOnboardingApplicationRepository applicationRepository,
        IApplicationUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _applicationRepository = applicationRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreateOnboardingApplicationResult> HandleAsync(
        CreateOnboardingApplicationCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(
            command.CustomerId,
            cancellationToken);

        if (customer is null)
        {
            throw new KeyNotFoundException(
                $"Customer '{command.CustomerId}' was not found.");
        }

        customer.StartOnboarding();

        var application = OnboardingApplication.Create(
            ApplicationNumber.Create(command.ApplicationNumber),
            customer.Id);

        await _applicationRepository.AddAsync(application, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateOnboardingApplicationResult(
            application.Id,
            application.ApplicationNumber.Value);
    }
}

public sealed record CreateOnboardingApplicationResult(
    long ApplicationId,
    string ApplicationNumber);