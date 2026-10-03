using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.CreateCustomer;

/// <summary>
/// Creates a customer with a primary residential address.
///
/// The customer's own OIDC SubjectId and the BranchId are deliberately not part
/// of this command: neither may be supplied by the calling staff user. The
/// SubjectId is linked later by an explicit identity-association step.
/// ManagingAgentUserId is the acting agent (token subject), set by the API layer.
/// </summary>
public sealed record CreateCustomerCommand(
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    CustomerType CustomerType,
    ResidentialAddress ResidentialAddress,
    string ManagingAgentUserId);

public sealed record ResidentialAddress(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string CountryCode);