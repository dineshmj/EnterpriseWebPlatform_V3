using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;

public sealed class CreateOnboardingApplicationCommandHandler
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IOnboardingApplicationRepository _applicationRepository;
    private readonly IApplicationUnitOfWork _unitOfWork;
    private readonly IApplicationNumberGenerator _applicationNumberGenerator;
    private readonly TimeProvider _clock;

    public CreateOnboardingApplicationCommandHandler(
        ICustomerRepository customerRepository,
        IOnboardingApplicationRepository applicationRepository,
        IApplicationUnitOfWork unitOfWork,
        IApplicationNumberGenerator applicationNumberGenerator,
        TimeProvider clock)
    {
        _customerRepository = customerRepository;
        _applicationRepository = applicationRepository;
        _unitOfWork = unitOfWork;
        _applicationNumberGenerator = applicationNumberGenerator;
        _clock = clock;
    }

    /// <summary>
    /// Starts onboarding for a customer. This deliberately changes TWO aggregates in
    /// one transaction (Customer → ONBOARDING, a new OnboardingApplication): both live
    /// in this bounded context and database, and the business requires that neither
    /// exists without the other. Every other command changes exactly one aggregate.
    /// </summary>
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

        var now = _clock.GetUtcNow();

        customer.StartOnboarding(now);

        var application = OnboardingApplication.Create(
            ApplicationNumber.Issue(
                await _applicationNumberGenerator.GetNextAsync(cancellationToken),
                now),
            customer.Id,
            BranchCode.Create(command.BranchCode),
            now);

        await _applicationRepository.AddAsync(application, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateOnboardingApplicationResult(
            application.Id,
            application.ApplicationNumber.Value,
            application.ApplicationRef);
    }
}

public sealed record CreateOnboardingApplicationResult(
    long ApplicationId,
    string ApplicationNumber,
    Guid ApplicationRef);