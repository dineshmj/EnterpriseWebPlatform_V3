using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;

public sealed class UpdateCustomerCommandHandler
{
    private readonly ICustomerRepository _customerRepository;

    private readonly IApplicationUnitOfWork _unitOfWork;

    private readonly TimeProvider _clock;

    public UpdateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        IApplicationUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _customerRepository = customerRepository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task HandleAsync(
        UpdateCustomerCommand command,
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

        if (customer.Version != command.ExpectedVersion)
        {
            throw new ConcurrencyConflictException(
                "The customer was modified by another request.");
        }

        var now = _clock.GetUtcNow();

        customer.ChangeContactDetails(
            EmailAddress.Create(command.Email),
            PhoneNumber.Create(command.PhoneNumber),
            now);

        customer.Rename(PersonName.Create(command.FirstName, command.LastName), now);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}