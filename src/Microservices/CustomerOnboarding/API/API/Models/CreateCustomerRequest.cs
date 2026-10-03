using System.ComponentModel.DataAnnotations;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

public sealed class CreateCustomerRequest
{
    [Required]
    [MaxLength(100)]
    public string FirstName { get; init; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; init; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string PhoneNumber { get; init; } = string.Empty;

    [Required]
    [EnumDataType(typeof(CustomerType))]
    public CustomerType CustomerType { get; init; }

    // SubjectId and BranchId are intentionally NOT accepted from the caller:
    // a staff user must not be able to link a customer to an arbitrary identity
    // or place it in an arbitrary branch.

    [Required]
    public ResidentialAddressRequest? ResidentialAddress { get; init; }
}

public sealed class ResidentialAddressRequest
{
    [Required]
    [MaxLength(200)]
    public string AddressLine1 { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? AddressLine2 { get; init; }

    [Required]
    [MaxLength(100)]
    public string City { get; init; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string State { get; init; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string PostalCode { get; init; } = string.Empty;

    [Required]
    [StringLength(2, MinimumLength = 2)]
    public string CountryCode { get; init; } = string.Empty;
}
