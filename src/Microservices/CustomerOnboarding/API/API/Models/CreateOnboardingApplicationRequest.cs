using System.ComponentModel.DataAnnotations;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

/// <summary>
/// The application number is issued by Customer Onboarding, not supplied by the
/// caller; any number a caller still sends is ignored.
/// </summary>
public sealed class CreateOnboardingApplicationRequest
{
    [Range(1, long.MaxValue)]
    public long CustomerId { get; init; }
}
