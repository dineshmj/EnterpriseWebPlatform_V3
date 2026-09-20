using System.ComponentModel.DataAnnotations;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

public sealed class SubmitOnboardingApplicationRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }
}