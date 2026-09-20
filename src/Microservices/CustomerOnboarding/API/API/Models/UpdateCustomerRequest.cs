using System.ComponentModel.DataAnnotations;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

public sealed class UpdateCustomerRequest
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

    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}