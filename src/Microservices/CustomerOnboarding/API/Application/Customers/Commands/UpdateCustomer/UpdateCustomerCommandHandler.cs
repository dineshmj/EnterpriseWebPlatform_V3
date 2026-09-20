using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;

public sealed class UpdateCustomerCommandHandler
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IApplicationUnitOfWork _unitOfWork;

    public UpdateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        IApplicationUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _unitOfWork = unitOfWork;
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
            throw new InvalidOperationException(
                "The customer was modified by another request.");
        }

        customer.ChangeContactDetails(
            EmailAddress.Create(command.Email),
            PhoneNumber.Create(command.PhoneNumber));

        customer.UpdateName(command.FirstName, command.LastName);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}