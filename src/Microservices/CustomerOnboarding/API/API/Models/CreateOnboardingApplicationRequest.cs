using System.ComponentModel.DataAnnotations;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

public sealed class CreateOnboardingApplicationRequest
{
    [Range(1, long.MaxValue)]
    public long CustomerId { get; init; }

    [Required]
    [MaxLength(30)]
    public string ApplicationNumber { get; init; } = string.Empty;
}