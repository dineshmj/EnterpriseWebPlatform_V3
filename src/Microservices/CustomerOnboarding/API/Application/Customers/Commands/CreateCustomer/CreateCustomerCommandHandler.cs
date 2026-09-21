using System.Threading;
using System.Threading.Tasks;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandHandler
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ICustomerNumberGenerator _customerNumberGenerator;
    private readonly IApplicationUnitOfWork _unitOfWork;

    public CreateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        ICustomerNumberGenerator customerNumberGenerator,
        IApplicationUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _customerNumberGenerator = customerNumberGenerator;
        _unitOfWork = unitOfWork;
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

        var customer = Customer.Create(
            customerNumber,
            command.FirstName,
            command.LastName,
            EmailAddress.Create(command.Email),
            PhoneNumber.Create(command.PhoneNumber),
            command.CustomerType,
            command.SubjectId,
            command.BranchId);

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
