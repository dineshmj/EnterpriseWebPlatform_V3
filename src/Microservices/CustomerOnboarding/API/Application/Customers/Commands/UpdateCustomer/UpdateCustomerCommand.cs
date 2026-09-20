namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.UpdateCustomer;

public sealed record UpdateCustomerCommand(
    long CustomerId,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    long ExpectedVersion);