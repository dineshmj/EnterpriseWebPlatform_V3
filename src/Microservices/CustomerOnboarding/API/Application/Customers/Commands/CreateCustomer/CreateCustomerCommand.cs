using System;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;

public sealed record CreateCustomerCommand(
    string CustomerNumber,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    CustomerType CustomerType,
    Guid? SubjectId,
    long? BranchId);