using System;
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

    public Guid? SubjectId { get; init; }

    public long? BranchId { get; init; }
}
