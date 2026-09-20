using System.Threading;
using System.Threading.Tasks;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandHandler
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IApplicationUnitOfWork _unitOfWork;

    public CreateCustomerCommandHandler(
        ICustomerRepository customerRepository,
        IApplicationUnitOfWork unitOfWork)
    {
        _customerRepository = customerRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreateCustomerResult> HandleAsync(
        CreateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var customer = Customer.Create(
            CustomerNumber.Create(command.CustomerNumber),
            command.FirstName,
            command.LastName,
            EmailAddress.Create(command.Email),
            PhoneNumber.Create(command.PhoneNumber),
            command.CustomerType,
            command.SubjectId,
            command.BranchId);

        await _customerRepository.AddAsync(customer, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateCustomerResult(customer.Id, customer.CustomerNumber.Value);
    }
}

public sealed record CreateCustomerResult(
    long CustomerId,
    string CustomerNumber);