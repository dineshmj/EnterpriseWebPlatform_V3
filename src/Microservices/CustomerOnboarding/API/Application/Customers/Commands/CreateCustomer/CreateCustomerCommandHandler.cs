using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandHandler
{
    private readonly ICustomerRepository _customerRepository;

    private readonly ICustomerNumberGenerator _customerNumberGenerator;

    private readonly IApplicationUnitOfWork _unitOfWork;

    private readonly TimeProvider _clock;

    public CreateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        ICustomerNumberGenerator customerNumberGenerator,
        IApplicationUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _customerRepository = customerRepository;
        _customerNumberGenerator = customerNumberGenerator;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<CreateCustomerResult> HandleAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var sequenceNumber =
            await _customerNumberGenerator.GetNextAsync(
                cancellationToken);

        var customerNumber =
            CustomerNumber.Create(sequenceNumber);

        var now = _clock.GetUtcNow();

        var customer = Customer.Create(
            customerNumber,
            PersonName.Create(command.FirstName, command.LastName),
            EmailAddress.Create(command.Email),
            PhoneNumber.Create(command.PhoneNumber),
            command.CustomerType,
            command.ManagingAgentUserId,
            now);

        var address = command.ResidentialAddress;
        customer.AddAddress(
            CustomerAddress.Create(
                AddressType.Residential,
                PostalAddress.Create(
                    address.AddressLine1,
                    address.AddressLine2,
                    address.City,
                    address.State,
                    address.PostalCode,
                    address.CountryCode),
                isPrimary: true,
                now),
            now);

        await _customerRepository.AddAsync(
            customer,
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(
            cancellationToken);

        return new CreateCustomerResult(
            customer.Id,
            customer.CustomerNumber.Value);
    }
}

public sealed record CreateCustomerResult(
    long CustomerId,
    string CustomerNumber);